#!/usr/bin/env python3
"""Capture fresh rule and model trajectories for an offline recovery pilot."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
import tempfile

from compare import HERE
from mixed_benchmark import DRAW, GRIFFOH, QUEEN, make_inputs, pressure_trace
from mixed_fixture import with_eradicator
from teacher import teacher
from train import hand_key
from twins_engine_reference import write_json


def capture_shard(fixture, args, output, cases, variants):
    with teacher(fixture, args.windbot, output, mixed=True) as capture:
        for case in cases:
            for mode, model in variants.items():
                label = f"{case['id']}-{mode}"
                guide = capture(case, label, model, recovery_model=args.recovery_model)
                turns = [i for i, event in enumerate(guide["trace"]) if event[0] == "NewTurn"]
                pressure = pressure_trace(guide["trace"])
                events = guide.pop("trace")[turns[0]:turns[1]]
                activations = [i for i, e in enumerate(events) if e[0] == "Chaining" and e[2] == 1]
                landed = {evt["event"] for evt in pressure["activations"]
                          if evt["landed"] and evt["card"] not in {GRIFFOH, QUEEN}}
                hard = {evt["event"] for evt in pressure["activations"]
                        if evt["landed"] and evt["card"] not in {*DRAW, GRIFFOH, QUEEN}}
                guide["recoveryOrdinals"] = [i for i, event in enumerate(activations) if event in landed]
                guide["hardRecoveryOrdinals"] = [i for i, event in enumerate(activations) if event in hard]
                guide["mixed"] = bool(case["opening_traps"])
                guide["opponent_draws"] = pressure["opponent_draws"]
                guide["hard_hits"] = pressure["disruptions"]
                write_json(output / f"{label}.json", {"id": label, "policy": mode, "input": case, "guide": guide})
                print(label, guide["score"], guide["hard_hits"], guide["opponent_draws"], flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for option in ("fixture", "windbot", "output"):
        p.add_argument(f"--{option}", required=True, type=Path)
    p.add_argument("--seed", type=int, default=202611263)
    p.add_argument("--per-band", type=int, default=2)
    p.add_argument("--jobs", type=int, default=6)
    p.add_argument("--exclude", type=Path, action="append", default=[])
    p.add_argument("--model", type=Path, help="capture only this model's policy")
    p.add_argument("--recovery-model", type=Path, help="also load the current recovery overlay")
    p.add_argument("--inputs-in", type=Path, help="capture these already frozen duel inputs")
    args = p.parse_args()
    if args.recovery_model and not args.model:
        p.error("--recovery-model requires --model")
    reserved = {hand_key(case["hand"]) for path in args.exclude for case in json.loads(path.read_text())}
    cases = (json.loads(args.inputs_in.read_text(encoding="utf-8"))
             if args.inputs_in else make_inputs(args.seed, args.per_band, [], reserved))
    hands = [hand_key(case["hand"]) for case in cases]
    ids = [case["id"] for case in cases]
    if not hands or len(set(hands)) != len(hands) or reserved.intersection(hands):
        p.error("capture requires distinct starting hands outside the exclusions")
    if len(set(ids)) != len(ids) or any(not re.fullmatch(r"[a-z0-9-]+", id) for id in ids):
        p.error("capture requires unique case IDs using lowercase letters, numbers or hyphens")
    if args.jobs < 1:
        p.error("jobs must be positive")
    variants = {"current": args.model.resolve(strict=True)} if args.model else {
        "rule": None, "v1": HERE / "twins-branch-ranker.tsv", "v2": HERE / "twins-branch-ranker-guided.tsv",
    }
    args.output.mkdir(parents=True)
    output = args.output.resolve()
    jobs = min(args.jobs, len(cases))
    with tempfile.TemporaryDirectory(prefix="twins-v3-source-") as tmp:
        fixture, _ = with_eradicator(args.fixture.resolve(), Path(tmp) / "fixture")
        with ThreadPoolExecutor(max_workers=jobs) as pool:
            workers = []
            for i in range(jobs):
                shard = output / f"shard-{i}"
                shard.mkdir()
                workers.append(pool.submit(capture_shard, fixture, args, shard, cases[i::jobs], variants))
            for worker in workers:
                worker.result()
    output_paths = sorted(output.glob("shard-*/*.json"))
    guides = [json.loads(path.read_text()) for path in output_paths]
    write_json(output / "guides.json", sorted(guides, key=lambda r: r["id"]))


if __name__ == "__main__":
    main()
