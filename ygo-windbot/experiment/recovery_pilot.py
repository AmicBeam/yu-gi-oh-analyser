#!/usr/bin/env python3
"""Run and resume an offline CPU pilot from captured mixed-pressure trajectories."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from hashlib import sha256
import json
from pathlib import Path
import platform
import re
import subprocess
import tempfile
from time import monotonic

from train import MODEL_FILE, branch_pairs, export_model, fit, hand_key
from twins_engine_reference import EngineReference, write_json


class ReplayDivergence(RuntimeError):
    """A settled OCGCore replay disagrees with the recorded live outcome."""


def replay_case(item, args):
    case, guide = item["input"], item["guide"]
    with tempfile.TemporaryDirectory(prefix="twins-pilot-") as directory:
        engine = EngineReference(Path(directory), args.fixture, args.windbot, args.output, args.reference_ms,
                                 scorer=args.scorer)
        data = engine.engine(case, None, training_attempts=args.attempts, coach=guide)
        demonstration = data.get("demonstration")
        if demonstration is None:
            raise RuntimeError("the recorded duel did not settle")
        terminals, runs = (data["training"][key] for key in ("terminals", "runs"))
        scores = engine.score(terminals)
        witness = demonstration["terminal"]
        if (scores[witness] != guide["score"] or terminals[witness]["position"]["board"] != guide["board"]
            or demonstration["opponent_draws"] != guide["opponent_draws"]):
            raise ReplayDivergence(f"recorded board, score or draw count diverged "
                                   f"({scores[witness]} vs {guide['score']}, "
                                   f"{demonstration['opponent_draws']} vs {guide['opponent_draws']} draws)")
        values = [round(score - args.draw_cost * terminal.get("opponent_draws", 0), 3)
                  for score, terminal in zip(scores, terminals)]
        return {"id": item["id"], "policy": item["policy"], "group": list(hand_key(case["hand"])),
                "regime": "pressured" if guide.get("mixed") else "clean",
                "hard_hits": guide["hard_hits"], "live_draws": guide["opponent_draws"],
                "guide_score": guide["score"], "pivots": demonstration["pivots"],
                "attempts": data["attempts"], "settled": len(runs), "search_ms": data["reference_search_ms"],
                "unsupported_prompts": data["unsupported_prompts"],
                "best_board_score": max(scores), "best_adjusted_score": max(values),
                "pairs": list(branch_pairs(runs, values))}


def fit_split(done, output, seed, *, validation=None):
    # Mode variants of a hand stay on the same side.
    groups = sorted({tuple(row["group"]) for row in done if row["pairs"]})
    if validation is None:
        validation = set(groups[::5]) if len(groups) >= 5 else set()
    train = [pair for row in done if tuple(row["group"]) not in validation for pair in row["pairs"]]
    val = [pair for row in done if tuple(row["group"]) in validation for pair in row["pairs"]]
    if not train:
        return len(train), len(val), None, None
    weights, accuracy = fit(train, seed)
    if val:
        from train import features

        def margin(pair):
            high, low = (features(pair[k]) for k in ("high", "low"))
            high.subtract(low)
            return sum(weights[id] * value for id, value in high.items())

        validation_accuracy = round(sum(margin(pair) > 0 for pair in val) / len(val), 4)
    else:
        validation_accuracy = None
    export_model(output / MODEL_FILE, weights)
    return len(train), len(val), round(accuracy, 4), validation_accuracy


def summarize(output, shards, total, processed, elapsed, seed, quarantined):
    done = [json.loads(path.read_text(encoding="utf-8")) for path in sorted(shards.glob("*.json"))]
    train, val, accuracy, validation = fit_split(done, output, seed)
    windows = {(tuple(row["group"]), pair["prefix"]) for row in done for pair in row["pairs"]}
    hard_windows = {(tuple(row["group"]), pair["prefix"]) for row in done for pair in row["pairs"]
                    if pair["high"].get("after_hard_hit") and pair["low"].get("after_hard_hit")}
    result = {
        "expected_guides": total, "completed_guides": len(done), "processed_this_run": processed,
        "quarantined_guides": quarantined,
        "os": platform.platform(), "seconds_this_run": round(elapsed, 1),
        "search_seconds_sum": round(sum(row["search_ms"] for row in done) / 1000, 1),
        "attempts": sum(row["attempts"] for row in done), "settled": sum(row["settled"] for row in done),
        "guide_hard_hits": sum(row["hard_hits"] for row in done),
        "distinct_comparable_windows": len(windows), "distinct_hard_hit_windows": len(hard_windows),
        "pairs": sum(len(row["pairs"]) for row in done),
        "train_pairs": train, "validation_pairs": val, "fit_accuracy": accuracy, "validation_accuracy": validation,
        "by_policy": {p: {"verified": sum(row["policy"] == p for row in done),
                          "pairs": sum(len(row["pairs"]) for row in done if row["policy"] == p)}
                      for p in sorted({row["policy"] for row in done})},
    }
    write_json(output / "summary.json", result)
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for field in ("guides", "fixture", "windbot", "output"):
        p.add_argument(f"--{field}", type=Path, required=True)
    p.add_argument("--scorer", type=Path)
    p.add_argument("--jobs", type=int, default=6)
    p.add_argument("--attempts", type=int, default=500)
    p.add_argument("--reference-ms", type=int, default=25000)
    p.add_argument("--draw-cost", type=float, default=0.5)
    p.add_argument("--seed", type=int, default=202611263)
    p.add_argument("--stop-after", type=int, help="process at most this many unfinished guides before checkpointing")
    p.add_argument("--quarantine-divergence", action="store_true",
                   help="checkpoint settled replay mismatches separately from verified samples")
    args = p.parse_args()
    if min(args.jobs, args.attempts, args.reference_ms, args.stop_after or 1) < 1:
        p.error("budgets and jobs must be positive")
    source = args.guides.read_bytes()
    cases = json.loads(source)
    if not cases or len({item["id"] for item in cases}) != len(cases) or any(
        not re.fullmatch(r"[a-z0-9-]+", item["id"]) for item in cases
    ):
        p.error("expected unique guide IDs containing lowercase letters, numbers or hyphens")
    output = args.output.resolve()
    shards = output / "shards"
    shards.mkdir(parents=True, exist_ok=True)
    manifest = output / "manifest.json"
    config = {"guides_sha256": sha256(source).hexdigest(), "attempts": args.attempts,
              "reference_ms": args.reference_ms, "draw_cost": args.draw_cost, "seed": args.seed,
              "cards_sha256": sha256((args.fixture / "cards.cdb").read_bytes()).hexdigest(),
              "windbot_sha256": sha256(args.windbot.read_bytes()).hexdigest()}
    if manifest.exists():
        if json.loads(manifest.read_text(encoding="utf-8")) != config:
            p.error("resume options differ from the saved pilot")
    else:
        write_json(manifest, config)
    quarantine = output / "quarantine"
    if args.quarantine_divergence:
        quarantine.mkdir(exist_ok=True)
    discarded = {path.stem for path in quarantine.glob("*.json")}
    if discarded and not args.quarantine_divergence:
        p.error("this checkpoint contains quarantined guides; resume with --quarantine-divergence")
    ids = {case["id"] for case in cases}
    if not discarded <= ids or any((shards / f"{id}.json").exists() for id in discarded):
        p.error("quarantine does not match the guide list or overlaps verified shards")
    remaining = [case for case in cases if case["id"] not in discarded
                 and not (shards / f'{case["id"]}.json').exists()][:args.stop_after]
    started = monotonic()
    errors = []
    processed = 0
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        pending = {pool.submit(replay_case, case, args): case["id"] for case in remaining}
        for future in as_completed(pending):
            identifier = pending[future]
            try:
                row = future.result()
            except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
                if args.quarantine_divergence and isinstance(error, ReplayDivergence):
                    write_json(quarantine / f"{identifier}.json", {
                        "id": identifier, "kind": "replay_divergence", "error": str(error),
                    })
                    discarded.add(identifier)
                    print(identifier, "quarantined:", str(error), flush=True)
                    continue
                errors.append({"id": identifier, "error": str(error)})
                print(identifier, str(error), flush=True)
                continue
            write_json(shards / f"{identifier}.json", row)
            print(identifier, row["pivots"], len(row["pairs"]), flush=True)
            processed += 1
    write_json(output / "errors.json", errors)
    result = summarize(output, shards, len(cases), processed, monotonic() - started, args.seed, len(discarded))
    print(json.dumps(result), flush=True)
    return int(bool(errors or (args.stop_after is None and result["completed_guides"] + len(discarded) != len(cases))))


if __name__ == "__main__":
    raise SystemExit(main())
