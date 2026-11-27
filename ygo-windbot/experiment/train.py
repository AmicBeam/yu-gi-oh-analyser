#!/usr/bin/env python3
"""Generate OCGCore training pairs and export a CPU linear branch ranker."""

import argparse
from collections import Counter, defaultdict
from contextlib import nullcontext
import json
import math
from pathlib import Path
import random
import sys
import tempfile

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "tests"))

from compare_twins_interference import HAND_TRAPS, candidates, interference_outcome
from teacher import teacher
from twins_engine_reference import EngineReference, write_json

ZONES = ("board", "hand", "grave", "extra")
ALIASES = {9205574: 9205573, 36609519: 36609518}
MODEL_FILE = "twins-branch-ranker.tsv"


def read_deck():
    lines = (HERE.parent / "Decks" / "AI_Twins.ydk").read_text().splitlines()
    start, extra, end = map(lines.index, ("#main", "#extra", "!side"))
    return list(map(int, lines[start + 1:extra])), list(map(int, lines[extra + 1:end]))


def hand_key(hand):
    return tuple(sorted(hand))


def features(p):
    result = Counter()
    for slot, key in enumerate(ZONES):
        # WindBot tracks whether each extra-deck identity is still available.
        cards = p[key] if key != "extra" else {ALIASES.get(card, card) for card in p[key]}
        for card in cards:
            if card:
                result[ALIASES.get(card, card), slot] += 1
    result.update({
        (0, 0): min(5, p["summons"]),
        (0, 1): p["draws"],
        (0, 2): sum(card in p["set"] for card in (98360333, 25156)),
        (0, 3): int(p["search_locked"]),
    })
    return result


def branch_pairs(runs, scores, tiebreaks=None):
    groups = defaultdict(dict)
    tiebreaks = tiebreaks or {}
    for terminal, branches in runs:
        tie = tiebreaks.get(terminal)
        rank = (scores[terminal], tie["score"] if tie else 0)
        for prefix, action, position in branches:
            observed = groups[prefix]
            if action not in observed or rank > observed[action][0]:
                observed[action] = (rank, position, tie)
    for prefix, branches in sorted(groups.items()):
        ranked = sorted(branches.items(), key=lambda entry: (-entry[1][0][0], -entry[1][0][1], entry[0]))
        if len(ranked) < 2:
            continue
        _, (best, high, high_tie) = ranked[0]
        for _, (score, low, low_tie) in ranked[1:]:
            gap = round(best[0] - score[0], 3)
            same_score = gap == 0 and high_tie is not None and low_tie is not None and best[1] > score[1]
            if (gap >= 0.2 or same_score) and features(high) != features(low):
                pair = {"prefix": prefix, "high": high, "low": low, "gap": gap}
                if same_score:
                    pair["tiebreak"] = {"high": high_tie, "low": low_tie}
                yield pair


def fit(pairs, seed):
    rows = []
    for pair in pairs:
        diff = features(pair["high"])
        diff.subtract(features(pair["low"]))
        rows.append([(key, value) for key, value in sorted(diff.items()) if value])
    rng = random.Random(seed)
    weights = Counter()
    for epoch in range(50):
        rng.shuffle(rows)
        step = 0.04 / (1 + epoch * 0.1)
        for row in rows:
            margin = sum(weights[key] * value for key, value in row)
            gradient = step / (1 + math.exp(max(-30, min(30, margin))))
            for key, value in row:
                weights[key] += gradient * value - step * 0.01 * weights[key]
    margins = [sum(weights[key] * value for key, value in row) for row in rows]
    return weights, sum(margin > 0 for margin in margins) / len(margins)


def export_model(path, weights):
    rows = ["twins-branch-ranker-v1"]
    for card in sorted({0, *(card for card, _ in weights)}):
        rows.append("\t".join([str(card), *(f"{weights[card, slot]:.8f}" for slot in range(4))]))
    temp = path.with_suffix(".tmp")
    temp.write_text("\n".join(rows) + "\n", encoding="utf-8")
    temp.replace(path)


def training_cases(args, main, extra, seen, capture, saved):
    scenarios = tuple(s for s in HAND_TRAPS if s != "belle")
    schedule = ("clean", "nibiru", "droll", "belle") if args.guided else scenarios * 2
    generators = {scenario: candidates(scenario, main, extra, args.seed) for scenario in HAND_TRAPS}
    for i in range(args.hands):
        if saved is not None:
            case, guide = saved[i]["input"], saved[i]["guide"]
            if hand_key(case["hand"]) in seen:
                raise ValueError("saved guide opening overlaps another input")
            seen.add(hand_key(case["hand"]))
            yield case, guide
            continue
        scenario = schedule[i % len(schedule)]
        source = generators["ash" if scenario == "clean" else scenario]
        for retry in range(5 if args.guided and scenario != "clean" else 1):
            case = next(case for case in source if hand_key(case["hand"]) not in seen)
            seen.add(hand_key(case["hand"]))
            if scenario == "clean" or not args.guided and i % len(schedule) < len(scenarios):
                case["scenario"] = None
            guide = capture(case, f"{i}-{retry}") if capture else None
            if not guide or not case["scenario"] or guide["interference"]["landed"]:
                yield case, guide
                break
            print(f'Guide {i}-{retry} {scenario} missed: {guide["interference"]}', flush=True)
        else:
            raise RuntimeError(f"no landed {scenario} guide in five hands")


def verify_guide(result, guide, terminals, scores, scenario):
    demo = result.get("demonstration")
    if not demo:
        raise RuntimeError(f"OCGCore could not replay a settled rule demonstration for {scenario or 'clean'}")
    i = demo["terminal"]
    if scores[i] != guide["score"] or terminals[i]["position"]["board"] != guide["board"]:
        raise RuntimeError(f"rule demonstration diverged from live terminal for {scenario or 'clean'}: "
                           f'{scores[i]} vs {guide["score"]}')
    if scenario and not interference_outcome(demo["trace"], scenario)["landed"]:
        raise RuntimeError(f"demonstration did not reproduce the live {scenario} outcome")
    return {"score": scores[i], "pivots": demo["pivots"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--windbot", type=Path, required=True, help="rule baseline WindBot.exe for terminal scoring")
    parser.add_argument("--output", type=Path, required=True, help="new output directory")
    parser.add_argument("--holdout", type=Path, action="append", help="additional hands to exclude")
    parser.add_argument("--guided", action="store_true", help="seed search with rule-mode duel replays")
    parser.add_argument("--guides-in", type=Path, help="replay saved guided-inputs.json instead of capturing live")
    parser.add_argument("--hands", type=int, default=12)
    parser.add_argument("--attempts", type=int, default=200, help="max OCGCore rollouts per hand")
    parser.add_argument("--reference-ms", type=int, default=10000, help="maximum search time per hand")
    parser.add_argument("--seed", type=int, default=20261127)
    args = parser.parse_args()
    if min(args.hands, args.attempts, args.reference_ms) <= 0:
        parser.error("hands, attempts and reference-ms must be positive")

    main_deck, extra = read_deck()
    holdouts = [HERE / "holdout.json", *(args.holdout or [])]
    if args.guided:
        holdouts.append(HERE / "holdout-v2.json")
    reserved = {hand_key(case["hand"]) for path in holdouts for case in json.loads(path.read_text())}
    saved = json.loads(args.guides_in.read_text()) if args.guides_in else None
    if saved is not None and (not args.guided or len(saved) != args.hands):
        parser.error("--guides-in requires --guided and a matching --hands count")

    out = args.output.resolve()
    out.mkdir(parents=True)
    context = (teacher(args.fixture, args.windbot, out)
               if args.guided and saved is None else nullcontext(None))
    with context as capture:
        cases = list(training_cases(args, main_deck, extra, set(reserved), capture, saved))
    inputs = [case for case, _ in cases]
    write_json(out / "training-inputs.json", inputs)
    if args.guided:
        write_json(out / "guided-inputs.json", [{"input": case, "guide": guide} for case, guide in cases])
    stats = []
    pairs = []
    with tempfile.TemporaryDirectory(prefix="twins-train-") as directory:
        work = Path(directory)
        (work / "node_modules").symlink_to(args.fixture.resolve() / "node_modules")
        engine = EngineReference(work, args.fixture, args.windbot, out, args.reference_ms)
        with (out / "pairs.jsonl").open("w", encoding="utf-8") as samples:
            for index, (case, guide) in enumerate(cases):
                result = engine.engine(case, case["scenario"], training_attempts=args.attempts, coach=guide)
                terminals, runs = (result["training"][key] for key in ("terminals", "runs"))
                scores = engine.score(terminals) if terminals else []
                demonstration = verify_guide(result, guide, terminals, scores, case["scenario"]) if guide else None
                generated = list(branch_pairs(runs, scores, {i: t["tiebreak"] for i, t in enumerate(terminals) if "tiebreak" in t}))
                for pair in generated:
                    samples.write(json.dumps({"case": index, **pair}, separators=(",", ":")) + "\n")
                pairs.extend(generated)
                stats.append({"hand": case["hand"], "scenario": case["scenario"], "attempts": result["attempts"],
                              "settled_runs": len(runs), "terminals": len(terminals), "pairs": len(generated),
                              "best_score": max(scores, default=None), "search_ms": result["reference_search_ms"],
                              "unsupported_prompts": result["unsupported_prompts"], **({"demonstration": demonstration} if guide else {})})
                print(f'{index + 1}/{len(inputs)} {case["scenario"] or "clean"}: {stats[-1]}', flush=True)
    if not pairs:
        raise RuntimeError("no distinct, differently scored branches were found")
    weights, accuracy = fit(pairs, args.seed)
    export_model(out / MODEL_FILE, weights)
    write_json(out / "training-summary.json", {
        "seed": args.seed, "max_attempts": args.attempts, "reference_ms": args.reference_ms, "pairs": len(pairs),
        "fit_pair_accuracy": round(accuracy, 4), "holdout_unique_hands": len(reserved), "cases": stats,
    })


if __name__ == "__main__":
    main()
