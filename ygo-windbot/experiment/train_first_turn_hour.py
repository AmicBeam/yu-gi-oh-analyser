#!/usr/bin/env python3
"""Train a first-turn branch ranker from balanced, replay-checked OCGCore rollouts."""

import argparse
from collections import Counter
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from datetime import datetime, timezone
from hashlib import sha256
from itertools import islice
import json
from pathlib import Path
import random
import signal
from statistics import mean
from subprocess import TimeoutExpired
from tempfile import TemporaryDirectory
from time import monotonic, time

from mixed_benchmark import FILL, HERE, TRAPS, make_case, pressure_trace
from recovery_pilot import fit_split
from train import branch_pairs, hand_key, read_deck
from twins_engine_reference import EngineReference, write_json

STARTERS = {36326160, 73810864, 82699999, 37582948, 61976639}
BANDS = (0, 1, 0, 2, 3, 0, 4, 5)


def cases(seed, seen, prefix):
    rng = random.Random(seed)
    main, extra = read_deck()
    fillers = [card for card in FILL for _ in range(3)]
    targets = list(TRAPS)
    rng.shuffle(targets)
    for index in range(10000):
        if index < len(targets):
            opening = [targets[index]]
        else:
            count = BANDS[(index - len(targets)) % len(BANDS)]
            opening = rng.sample(targets, count)
        case = make_case(f"{prefix}-{index:05}", opening, main, extra, fillers, rng, seen)
        # Forced probes use playable starts to expose the card's real windows.
        if index < len(targets):
            while not STARTERS.intersection(case["hand"]):
                case = make_case(case["id"], opening, main, extra, fillers, rng, seen)
        yield {**case, "draw_seed": case["duel_seed"][0], "mixed_search": True}


def train_case(case, args, budget_ms):
    with TemporaryDirectory(prefix="twins-first-") as temp:
        engine = EngineReference(Path(temp), args.fixture, args.windbot, args.output, budget_ms, scorer=args.scorer)
        result = engine.engine(case, None, training_attempts=args.attempts)
        terminals = result["training"]["terminals"]
        runs = result["training"]["runs"]
        if not result["verified"]:
            return {"case": case, "error": "No scored terminal passed complete replay", "attempts": result["attempts"]}
        raw = engine.score(terminals)
    scores = [round(score - terminal["opponent_draws"], 3) for score, terminal in zip(raw, terminals)]
    probes = [pressure_trace(trace) for trace in result["coverage"]]
    activated = sorted({item["card"] for probe in probes for item in probe["activations"]})
    landed = sorted({item["card"] for probe in probes for item in probe["activations"] if item["landed"]})
    return {"case": case, "group": hand_key(case["hand"]), "pairs": list(branch_pairs(runs, scores)),
            "attempts": result["attempts"], "settled": len(runs), "best_score": max(scores),
            "witness_score": scores[result["witness"]], "activated": activated, "landed": landed,
            "unsupported_prompts": result["unsupported_prompts"]}


def status(args, rows, started, deadline, state, fit_model=False):
    done = [row for row in rows if "pairs" in row]
    bands = {}
    for index in range(6):
        group = [row for row in done if len(row["case"]["opening_traps"]) == index]
        bands[str(index)] = {"hands": len(group), "pairs": sum(len(row["pairs"]) for row in group),
                             "best_score_mean": round(mean(row["best_score"] for row in group), 3) if group else None}
    result = {"status": state, "started": datetime.fromtimestamp(started, timezone.utc).isoformat(),
              "deadline": datetime.fromtimestamp(deadline, timezone.utc).isoformat(),
              "elapsed_seconds": round(time() - started, 1), "completed": len(done), "quarantined": len(rows) - len(done),
              "attempts": sum(row["attempts"] for row in done), "settled": sum(row["settled"] for row in done),
              "bands": bands, "target_cards": list(TRAPS),
              "opening_cards": dict(Counter(c for row in done for c in row["case"]["opening_traps"])),
              "activation_cards": dict(Counter(c for row in done for c in row["activated"])),
              "landed_cards": dict(Counter(c for row in done for c in row["landed"]))}
    if fit_model:
        train, val, accuracy, validation = fit_split(done, args.output, args.seed)
        result.update(train_pairs=train, validation_pairs=val, fit_accuracy=accuracy, validation_accuracy=validation)
        write_json(args.output / "training-inputs.json", [row["case"] for row in done])
    write_json(args.output / "status.json", result)
    print(json.dumps(result, ensure_ascii=False), flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("fixture", "windbot", "scorer", "output"):
        p.add_argument(f"--{name}", type=Path, required=True)
    p.add_argument("--seconds", type=int, default=3600)
    p.add_argument("--attempts", type=int, default=300)
    p.add_argument("--search-ms", type=int, default=9000)
    p.add_argument("--jobs", type=int, default=8)
    p.add_argument("--seed", type=int, default=2026112799)
    p.add_argument("--exclude", action="append", type=Path, default=[])
    args = p.parse_args()
    if min(args.seconds, args.attempts, args.search_ms, args.jobs) < 1:
        p.error("training budgets and jobs must be positive")
    args.output.mkdir(parents=True)
    shards = args.output / "shards"
    shards.mkdir()
    seen = {hand_key(row["hand"]) for file in [HERE / "holdout.json", HERE / "holdout-v2.json", *args.exclude]
            for row in json.loads(file.read_text())}
    holdout = list(islice(cases(args.seed + 1, set(seen), "holdout"), len(TRAPS) + 16))
    # Freeze both cohorts against the same starting-hand exclusion set.
    seen.update(hand_key(case["hand"]) for case in holdout)
    write_json(args.output / "holdout-inputs.json", holdout)
    inputs = cases(args.seed, seen, "train")
    digest = lambda path: sha256(path.read_bytes()).hexdigest()
    write_json(args.output / "manifest.json", {
        "seed": args.seed, "seconds": args.seconds, "jobs": args.jobs, "attempts": args.attempts,
        "search_ms": args.search_ms, "cards_sha256": digest(args.fixture / "cards.cdb"),
        "windbot_sha256": digest(args.windbot), "scorer_sha256": digest(args.scorer),
        "deck_sha256": digest(HERE.parent / "Decks" / "AI_Twins.ydk"),
        "search_sha256": digest(HERE.parent / "tests" / "twins-engine-reference.cjs"),
        "pressure_sha256": digest(HERE.parent / "tests" / "twins-mixed-interference.cjs")})
    started = time()
    cutoff = monotonic() + args.seconds
    stop_requested = False

    def stop_after_pending(signum, frame):
        nonlocal stop_requested
        stop_requested = True

    signal.signal(signal.SIGINT, stop_after_pending)
    signal.signal(signal.SIGTERM, stop_after_pending)
    rows, pending = [], {}
    status(args, rows, started, started + args.seconds, "running")
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        while True:
            remaining = cutoff - monotonic()
            while not stop_requested and len(pending) < args.jobs and remaining > 9:
                case = next(inputs)
                pending[pool.submit(train_case, case, args, min(args.search_ms, int((remaining - 8) * 1000)))] = case
                remaining = cutoff - monotonic()
            if not pending:
                break
            finished, _ = wait(pending, return_when=FIRST_COMPLETED)
            for future in finished:
                case = pending.pop(future)
                try:
                    row = future.result()
                except (RuntimeError, OSError, TimeoutError, TimeoutExpired, ValueError, IndexError) as error:
                    row = {"case": case, "error": str(error)}
                rows.append(row)
                write_json(shards / f"{case['id']}.json", row)
                if len(rows) % 16 == 0:
                    status(args, rows, started, started + args.seconds, "running", fit_model=len(rows) % 128 == 0)
    completed = any(row.get("pairs") for row in rows)
    ended = time() if stop_requested else started + args.seconds
    status(args, rows, started, ended, "completed" if completed else "no_pairs", fit_model=completed)
    if stop_requested:
        write_json(args.output / "stop.json", {"reason": "stop-after-current-batch", "finished_shards": len(rows),
                                             "elapsed_seconds": round(ended - started, 1)})
    return 0 if completed else 1


if __name__ == "__main__":
    raise SystemExit(main())
