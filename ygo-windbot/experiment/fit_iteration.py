#!/usr/bin/env python3
"""Update the current ranker using its new trajectories and the previous pilot data."""

import argparse
from collections import defaultdict
from hashlib import sha256
import json
from pathlib import Path
import random

from recovery_pilot import fit_split
from train import MODEL_FILE
from twins_engine_reference import write_json


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def verified_shards(path):
    summary = read(path / "summary.json")
    errors = read(path / "errors.json")
    shards = [read(file) for file in sorted((path / "shards").glob("*.json"))]
    quarantine = [read(file) for file in sorted((path / "quarantine").glob("*.json"))]
    ids = {row["id"] for row in shards + quarantine}
    if (errors or len(ids) != len(shards) + len(quarantine) or len(ids) != summary["expected_guides"]
        or len(shards) != summary["completed_guides"] or len(quarantine) != summary.get("quarantined_guides", 0)
        or any(row["kind"] != "replay_divergence" for row in quarantine)):
        raise ValueError(f"{path} has incomplete or failed trajectories")
    return read(path / "manifest.json"), shards


def groups(rows, *, with_pairs=False):
    return {tuple(row["group"]) for row in rows if row["pairs"] or not with_pairs}


def stratified_validation(rows, seed):
    """Keep opening hands together and keep both sides of each usable regime populated."""
    strata = defaultdict(set)
    for row in rows:
        cohort = strata[row["regime"]]
        if row["pairs"]:
            cohort.add(tuple(row["group"]))
    if any(len(hands) < 2 for hands in strata.values()):
        raise ValueError("each new regime needs two distinct hands with ranking pairs for train and validation")
    rng = random.Random(seed)
    validation = set()
    for _, hands in sorted(strata.items()):
        shuffled = sorted(hands)
        rng.shuffle(shuffled)
        target = max(1, len(hands) // 5)
        for hand in shuffled:
            if len(hands & validation) >= target:
                break
            if all(other - (validation | {hand}) for other in strata.values()):
                validation.add(hand)
    if any(not (hands & validation) or not (hands - validation) for hands in strata.values()):
        raise ValueError("could not split both sides of every regime without separating a starting hand")
    return validation


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("previous", "fresh", "incumbent", "output"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument("--fresh-guides", type=Path, help="original guide file for a legacy fresh run without regime tags")
    parser.add_argument("--supplement", type=Path, action="append", default=[],
                        help="additional verified search run with regime tags; repeatable")
    parser.add_argument("--seed", type=int, default=202611271)
    args = parser.parse_args()

    previous, old = verified_shards(args.previous)
    fresh, new = verified_shards(args.fresh)
    shared = ("draw_cost", "cards_sha256", "windbot_sha256")
    if any(previous[key] != fresh[key] for key in shared):
        parser.error("training runs use different scoring conditions")
    if any("regime" not in row for row in new):
        if not args.fresh_guides or sha256(args.fresh_guides.read_bytes()).hexdigest() != fresh["guides_sha256"]:
            parser.error("supply the exact --fresh-guides for a legacy search run")
        regimes = {item["id"]: "pressured" if item["guide"]["mixed"] else "clean" for item in read(args.fresh_guides)}
        for row in new:
            row.setdefault("regime", regimes[row["id"]])
    for path in args.supplement:
        config, extra = verified_shards(path)
        if any(previous[key] != config[key] for key in shared):
            parser.error(f"{path} uses different scoring conditions")
        new.extend(extra)
    if groups(old) & groups(new):
        parser.error("new trajectories share a starting hand with the old pilot")
    incumbent = args.incumbent.read_bytes()
    if incumbent != (args.previous / MODEL_FILE).read_bytes():
        parser.error("the incumbent differs from the previous pilot export")
    if any(row["policy"] != "current" for row in new):
        parser.error("new trajectories must come from the incumbent policy")
    if any(row.get("regime") not in ("clean", "pressured") for row in new):
        parser.error("new trajectories must be tagged clean or pressured")
    old_groups = sorted(groups(old, with_pairs=True))
    validation = set(old_groups[::5] if len(old_groups) >= 5 else ())
    try:
        validation.update(stratified_validation(new, args.seed))
    except ValueError as error:
        parser.error(str(error))
    args.output.mkdir(parents=True)
    train_count, val_count, fit_accuracy, validation_accuracy = fit_split(
        old + new, args.output, args.seed, validation=validation)
    candidate = (args.output / MODEL_FILE).read_bytes()
    result = {
        "old_guides": len(old), "new_guides": len(new), "new_guides_with_pairs": sum(bool(r["pairs"]) for r in new),
        "old_pairs": sum(len(r["pairs"]) for r in old), "new_pairs": sum(len(r["pairs"]) for r in new),
        "new_hard_hits": sum(r["hard_hits"] for r in new),
        "new_guide_scores_improved_in_search": sum(r["best_board_score"] > r["guide_score"] for r in new),
        "train_pairs": train_count, "validation_pairs": val_count, "fit_accuracy": fit_accuracy,
        "validation_accuracy": validation_accuracy,
        "fit_seed": args.seed, "draw_cost": previous["draw_cost"],
        "incumbent_sha256": sha256(incumbent).hexdigest(), "candidate_sha256": sha256(candidate).hexdigest(),
        "training_hands": sorted(groups(old) | groups(new)),
    }
    result["new_by_regime"] = {}
    for regime in sorted({row["regime"] for row in new}):
        rows = [row for row in new if row["regime"] == regime and row["pairs"]]
        train_rows = [row for row in rows if tuple(row["group"]) not in validation]
        val_rows = [row for row in rows if tuple(row["group"]) in validation]
        result["new_by_regime"][regime] = {
            "train_hands": len(groups(train_rows)), "validation_hands": len(groups(val_rows)),
            "train_pairs": sum(len(row["pairs"]) for row in train_rows),
            "validation_pairs": sum(len(row["pairs"]) for row in val_rows),
        }
    write_json(args.output / "summary.json", result)
    print(json.dumps({key: value for key, value in result.items() if key != "training_hands"}), flush=True)


if __name__ == "__main__":
    main()
