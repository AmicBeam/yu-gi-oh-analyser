#!/usr/bin/env python3
"""Compare the rule build and both rankers against seeded 0–5 opening hand traps."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import random
import re
import shutil
import tempfile

from compare import HERE, MODEL_ENV, run_variant
from compare_twins_interference import DRAGON_EXTRA, chain_resolution
from compare_twins_planner import available_port, await_port, background, fixed_deck, prepare_fixture
from mixed_fixture import with_eradicator
from train import hand_key, read_deck
from twins_engine_reference import write_json

DRAW = (23434538, 42141493, 84192580, 87126721, 47605577)
DOMINUS = (58053438, 40366667, 97045737, 42091632, 6325660)
BYSTIAL = (33854624, 6637331, 60242223, 72656408)
CROW, DISASTER, GRIFFOH, SWORD, RITUAL, DRAGON, MASTER = (24508238, 30316053, 97462632, 77456448, 33599853, 70088809, 72444406)
VEILER, QUEEN = 97268402, 65302903
TRAPS = (*DRAW, *DOMINUS, *BYSTIAL, CROW, DISASTER, GRIFFOH, DRAGON, 14558128, 27204311, 94145022,
         59438930, 10045474, 73642296, VEILER, QUEEN)
FILL = (33599853, 46986414, 74677422, 31305911, 7572887, 89631139, 33396948, 53129443,
        99785935, 76812113, 15025844, 33420078, 28279543, 55291359)
NEGATION = {58053438, 40366667, 97045737, 14558128, 10045474, 73642296, VEILER}
REMOVAL = {CROW, *BYSTIAL, 42091632, 6325660, 59438930, MASTER, DRAGON}


def make_inputs(seed, per_band, probe_ids, training):
    rng = random.Random(seed)
    main, extra = read_deck()
    targets = list(TRAPS)
    rng.shuffle(targets)
    fillers = [card for card in FILL for _ in range(3)]
    # Cards irrelevant to first-turn chain choices fill the remaining opening slots.
    pool_index = 0
    cases = []
    seen = set(training)
    for count in range(6):
        for i in range(per_band):
            group = [targets[(pool_index + offset) % len(targets)] for offset in range(count)]
            pool_index += count
            cases.append(make_case(f"mix-{count}-{i+1:02d}", group, main, extra, fillers, rng, seen))
    for card in probe_ids:
        cases.append(make_case(f"probe-{card}", [card], main, extra, fillers, rng, seen))
    return cases


def make_case(name, group, main, extra, fillers, rng, seen):
    while True:
        hand = rng.sample(main, 5)
        if hand_key(hand) not in seen:
            seen.add(hand_key(hand))
            break
    seed = rng.randrange(1 << 31)
    open_fillers = [RITUAL] if GRIFFOH in group and len(group) < 5 else []
    while len(open_fillers) + len(group) < 5:
        open_fillers.append(rng.choice([card for card in FILL if card not in open_fillers]))
    opp_hand = group + open_fillers
    rng.shuffle(opp_hand)
    rest = list(fillers)
    for card in open_fillers:
        rest.remove(card)
    # Later draws can legitimately add more traps. The test still bins by the initial five.
    rest = [SWORD, *TRAPS, *rng.sample(rest, 34 - len(TRAPS))]
    rng.shuffle(rest)
    return {"id": name, "hand": hand, "main": fixed_deck(main, hand, seed), "extra": extra,
            "opening_traps": group, "opponent": {"main": opp_hand + rest, "extra": DRAGON_EXTRA, "side": []},
            "duel_seed": [rng.getrandbits(32) for _ in range(8)]}


def has_move(events, code, source, dest, controller):
    return any(e[0] == "Move" and (not code or e[1] == code)
               and (source is None or e[2] == source) and (dest is None or e[3] == dest) and e[5] == controller
               for e in events)


def pressure_trace(trace):
    turns = [i for i, event in enumerate(trace) if event[0] == "NewTurn"]
    first = trace[turns[0]:turns[1]]
    activations = []
    for i, e in enumerate(first):
        if e[0] != "Chaining" or e[2] != 1 or e[1] not in {*TRAPS, SWORD, MASTER}:
            continue
        card, link = e[1], e[5]
        resolved, end = chain_resolution(first, i)
        start = next((j + 1 for j in range(i - 1, -1, -1) if first[j][0] == "ChainEnd"), 0)
        prior = next((old for old in reversed(first[start:i]) if old[0] == "Chaining" and old[2] == 0
                      and old[5] == link - 1), None)
        chain_end = next((j for j in range(end, len(first)) if first[j][0] == "ChainEnd"), len(first))
        picked = next((j for j in range(i - 1, start - 1, -1) if first[j][:2] == ["MixedPick", card]), i)
        window = first[picked:end + 1]
        targets = [x[2] for x in window if x[:2] == ["MixedTarget", card] and x[3:5] == [4, 0]]
        begin_solve = next((j for j in range(i, end + 1) if first[j][:2] == ["ChainSolving", link]), i)
        effects = first[begin_solve:end + 1]
        landed = False
        if resolved and card in DRAW:
            landed = True
        elif resolved and card in NEGATION:
            landed = prior is not None and any(x[:2] in (["ChainDisabled", prior[5]], ["ChainNegated", prior[5]])
                for x in first[i:chain_end])
            if card in (10045474, VEILER) and not landed and targets:
                landed = not any(has_move(window, target, 4, None, 0) for target in targets)
        elif resolved and card in (27204311, DISASTER):
            landed = any(x[0] == "Move" and x[1] == card and x[3] == 4 and x[7] == 1 for x in effects)
        elif resolved and card == 94145022:
            landed = True
        elif resolved and card == GRIFFOH:
            landed = has_move(effects, SWORD, 1, 8, 1)
        elif resolved and card == QUEEN:
            landed = has_move(effects, 97045737, 1, 2, 1)
        elif resolved and card == SWORD:
            landed = has_move(effects, 0, 8, 16, 0) or prior is not None and any(
                x[:2] in (["ChainDisabled", prior[5]], ["ChainNegated", prior[5]]) for x in first[i:chain_end])
            # Sword can resolve on a monster outside a chain. The legal target must stay on the field.
            if not landed and targets:
                landed = not any(has_move(window, target, 4, None, 0) for target in targets)
        elif resolved and card in REMOVAL:
            destinations = {
                CROW: (16, 32), DRAGON: (4, 16), MASTER: (4, 8), 59438930: (4, 16),
                42091632: (4, 2), 6325660: (4, 32),
            }
            src, dst = destinations.get(card, (16, 32))
            landed = has_move(effects, 0, src, dst, 0)
            if card == DRAGON:
                landed = landed and has_move(effects, MASTER, 64, 16, 1)
            if card == 42091632 and not landed:
                landed = has_move(effects, 0, 4, 64, 0)
        activations.append({"card": card, "link": link, "target_effect": prior[1] if prior else None,
                            "resolved": resolved, "landed": landed, "event": i, "end": chain_end})
    disruptive = [x for x in activations if x["landed"] and x["card"] not in {*DRAW, GRIFFOH, QUEEN}]
    cutoff = max((x["end"] for x in disruptive), default=None)
    return {"activations": activations, "disruptions": len(disruptive),
            "opponent_draws": sum(e[3] for e in first if e[0] == "Draw" and e[2] == 1),
            "own_summons_after_last_hit": sum(e[0] == "Move" and e[3] == 4 and e[7] == 0 and e[5] == 0
                                             and e[2] != 4 for e in first[cutoff + 1:]) if cutoff is not None else None,
            "own_sets_after_last_hit": sum(e[0] == "Move" and e[2] in (1, 2) and e[3] == 8
                                          and e[7] == 0 and bool(e[8] & 10)
                                          for e in first[cutoff + 1:]) if cutoff is not None else None}


def run_group(fixture, windbot, output, shard, ports, cases, variants, *, clean_controls=True, variant_env=None):
    env = {key: value for key, value in os.environ.items() if not key.startswith("TWINS_")}
    with tempfile.TemporaryDirectory(prefix="twins-mixed-") as directory:
        work = Path(directory)
        prepare_fixture(work, fixture, windbot, HERE.parent / "bots.json", *ports)
        (work / "data").unlink()
        shutil.copytree(fixture / "data", work / "data")
        with background(["node", "--require", str(HERE / "fixed-duel-seed.cjs"), "dist/index.js"],
                        work, output / f"server-{shard}.log", env) as server:
            await_port(ports[0], server)
            for case in cases:
                write_json(work / "duel-seed.json", case["duel_seed"])
                write_json(work / "opponent.json", case["opponent"])
                (work / "Decks" / "AI_Twins.ydk").write_text(
                    "#main\n" + "\n".join(map(str, case["main"])) + "\n#extra\n"
                    + "\n".join(map(str, case["extra"])) + "\n")
                row = {"id": case["id"], "hand": case["hand"], "opponent_hand": case["opponent"]["main"][:5],
                       "opening_traps": case["opening_traps"], "strategies": {}}
                for mode, model in variants.items():
                    results = {}
                    conditions = ("clean",)
                    if case["opening_traps"]:
                        conditions = ("clean", "hit") if clean_controls else ("hit",)
                    for condition in conditions:
                        label = f'{case["id"]}-{mode}-{condition}'
                        variables = {**env, "TWINS_MIXED_PRESSURE": condition, **(variant_env or {}).get(mode, {})}
                        if model:
                            variables[MODEL_ENV] = str(model)
                        variant = run_variant(work, output, label, ports, case, variables)
                        if "trace" in variant:
                            trace = json.loads((output / variant["trace"]).read_text())
                            opening = [card for event in trace if event[0] == "MixedOpening" for card in event[1]]
                            if opening and Counter(opening) != Counter(row["opponent_hand"]):
                                variant = {"error": "opponent opening differs from fixed input", "trace": variant["trace"]}
                            variant.update(pressure_trace(trace))
                        results[condition] = variant
                    row["strategies"][mode] = results
                write_json(output / f'{case["id"]}-result.json', row)
                print(row["id"], row["opening_traps"], {k: [x.get("score", x.get("error")) for x in v.values()]
                      for k, v in row["strategies"].items()}, flush=True)


def summarize(rows, variants):
    band_stats, cards, paired = {}, {}, {}
    for n in range(6):
        band = [r for r in rows if r["id"].startswith("mix-") and len(r["opening_traps"]) == n]
        summaries = {}
        for mode in variants:
            complete = [(r["strategies"][mode]["clean"], r["strategies"][mode].get("hit", r["strategies"][mode]["clean"]))
                        for r in band]
            complete = [(a, b) for a, b in complete if "score" in a and "score" in b]
            under_hit = [b for a, b in complete if b["disruptions"]]
            summaries[mode] = {"complete": len(complete), "attempted": len(band),
                "clean_mean": average([a["score"] for a, b in complete]),
                "pressure_mean": average([b["score"] for a, b in complete]),
                "mean_loss": average([a["score"] - b["score"] for a, b in complete]),
                "mean_opponent_draws": average([b["opponent_draws"] for a, b in complete]),
                "actual_hit_cases": len(under_hit),
                "actual_hit_mean": average([b["score"] for b in under_hit]),
                "built_after_hit": sum(b["own_summons_after_last_hit"] + b["own_sets_after_last_hit"] > 0 for b in under_hit),
                "max_wait_ms": max((b["max_main_gap_ms"] for a, b in complete), default=None)}
        band_stats[str(n)] = summaries
        paired[str(n)] = {
            f"{new}_vs_{old}": compare_scores(band, new, old)
            for new, old in (("v1", "rule"), ("v2", "rule"), ("v2", "v1"))
        }
    for mode in variants:
        held = [id for row in rows for id in row["opening_traps"]]
        executed = [x for r in rows for x in r["strategies"][mode].get("hit", {}).get("activations", [])]
        cards[mode] = {str(c): {"in_opening": held.count(c), "activated": sum(x["card"]==c for x in executed),
                               "landed": sum(x["card"]==c and x["landed"] for x in executed)}
                       for c in (*TRAPS, SWORD, MASTER)}
    return {"bands": band_stats, "paired": paired, "coverage": cards}


def compare_scores(rows, new, old):
    deltas = []
    for row in rows:
        use = "hit" if row["opening_traps"] else "clean"
        a, b = (row["strategies"][mode][use] for mode in (old, new))
        if "score" in a and "score" in b:
            deltas.append(round(b["score"] - a["score"], 3))
    return {"comparable": len(deltas), "higher": sum(d > 0 for d in deltas), "equal": deltas.count(0),
            "lower": sum(d < 0 for d in deltas), "mean_delta": average(deltas)}


def average(values):
    return round(sum(values) / len(values), 3) if values else None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("fixture", "windbot", "v1", "v2", "output"):
        p.add_argument(f"--{name}", required=True, type=Path)
    p.add_argument("--seed", type=int, default=202611261)
    p.add_argument("--per-band", type=int, default=4)
    p.add_argument("--jobs", type=int, default=3)
    p.add_argument("--probe", type=int, action="append", default=[])
    p.add_argument("--probes-only", action="store_true")
    p.add_argument("--training-inputs", type=Path, action="append", default=[])
    p.add_argument("--inputs-in", type=Path, help="repeat an already generated set of cases")
    p.add_argument("--case", action="append", help="select a named case; repeatable")
    args = p.parse_args()
    if min(args.jobs, args.per_band) < 1 or any(c not in TRAPS for c in args.probe):
        p.error("invalid cohort size, jobs, or probe card")
    if args.probes_only and not args.probe:
        p.error("--probes-only needs --probe")
    training = {hand_key(row["hand"]) for path in args.training_inputs for row in json.loads(path.read_text())}
    cases = (json.loads(args.inputs_in.read_text()) if args.inputs_in else
             make_inputs(args.seed, 0 if args.probes_only else args.per_band, args.probe, training))
    if args.case:
        if set(args.case) - {c["id"] for c in cases}:
            p.error("--case contains an unknown case ID")
        cases = [c for c in cases if c["id"] in args.case]
    if not cases or len({case["id"] for case in cases}) != len(cases):
        p.error("at least one unique case ID is required")
    if any(not re.fullmatch(r"[a-z0-9-]+", case["id"]) for case in cases):
        p.error("case IDs may contain lowercase letters, digits and hyphens")
    variants = {"rule": None, "v1": args.v1.resolve(strict=True), "v2": args.v2.resolve(strict=True)}
    output = args.output.resolve()
    output.mkdir(parents=True)
    write_json(output / "inputs.json", cases)
    jobs = min(args.jobs, len(cases))
    ports = set()
    while len(ports) < 2 * jobs:
        ports.add(available_port())
    pairs = iter(sorted(ports))
    with tempfile.TemporaryDirectory(prefix="twins-mixed-fixture-") as tmp:
        fixture, source = with_eradicator(args.fixture.resolve(), Path(tmp) / "fixture")
        with ThreadPoolExecutor(max_workers=jobs) as pool:
            futures = [pool.submit(run_group, fixture, args.windbot, output, index, pair, cases[index::jobs], variants)
                       for index, pair in enumerate(zip(pairs, pairs))]
            for future in futures:
                future.result()
    rows = [json.loads((output / f'{case["id"]}-result.json').read_text()) for case in cases]
    write_json(output / "report.json", rows)
    write_json(output / "summary.json", {"seed": args.seed, "jobs": jobs, "eradicator_script": source,
                                         "cases": len(cases), **summarize(rows, variants)})


if __name__ == "__main__":
    main()
