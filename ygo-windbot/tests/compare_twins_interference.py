#!/usr/bin/env python3
"""Freeze reference end boards for paired live duels with and without a hand trap."""

import argparse
import json
from pathlib import Path
import random
import shutil
import subprocess
import tempfile

from compare_twins_planner import available_port, await_port, background, fixed_deck, play, prepare_fixture
from twins_engine_reference import EngineReference, read_standard, write_json

HAND_TRAPS = {
    "nibiru": 27204311, "ash": 14558128, "dragon": 70088809,
    "droll": 94145022, "ogre": 59438930, "imperm": 10045474, "belle": 73642296,
}
DRAGON_EXTRA = [72444406, 16172067, 33698022, 90953320, 50954680]
MASTER = DRAGON_EXTRA[0]


def chain_resolution(events, index):
    link = events[index][5]
    for end in range(index + 1, len(events)):
        event = events[end]
        if event[0] == "ChainEnd":
            return False, end
        if event[:2] in (["ChainDisabled", link], ["ChainNegated", link]):
            return False, end
        if event[:2] == ["ChainSolved", link]:
            return True, end
    return False, len(events)


def read_interference(path, scenario):
    return interference_outcome(json.loads(path.read_text()), scenario)


def interference_outcome(events, scenario):
    marker = next((i for i, event in enumerate(events) if event[:2] == ["Interference", scenario]), None)
    if marker is None:
        return {"landed": False, "reason": "no qualifying window"}
    _, _, card, target, summons = events[marker]
    index = next((i for i in range(marker + 1, len(events))
                  if events[i][:3] == ["Chaining", card, 1]), None)
    result = {"landed": False, "card": card, "target": target, "summons": summons}
    if index is None:
        return {**result, "reason": "activation missing"}
    resolved, end = chain_resolution(events, index)
    if not resolved:
        return {**result, "reason": "hand trap did not resolve"}

    prior = next((e for e in reversed(events[:marker]) if e[0] == "Chaining" and e[2] == 0), None)
    end_of_chain = next((i for i in range(end, len(events)) if events[i][0] == "ChainEnd"), len(events))
    if scenario in ("ash", "imperm", "belle"):
        landed = prior is not None and any(
            e[:2] in (["ChainDisabled", prior[5]], ["ChainNegated", prior[5]])
            for e in events[index:end_of_chain]
        )
        if scenario == "belle":
            sunny = 93672138
            landed = landed and target == sunny and prior[1] == sunny and prior[4] == sunny * 16
        if scenario == "imperm" and not landed:
            target_pick = next((e for e in events[marker:end] if e[0] == "InterferenceTarget"), None)
            landed = target_pick is not None and not any(
                e[0] == "Move" and e[1] == target_pick[1] and e[2] == 4
                and e[5:7] == [0, target_pick[2]] for e in events[index:end]
            )
    elif scenario == "nibiru":
        landed = summons == 5 and any(e[:4] == ["Move", card, 2, 4] for e in events[index:end])
    elif scenario == "ogre":
        landed = any(e[:4] == ["Move", target, 4, 16] and e[5] == 0 for e in events[index:end])
    elif scenario == "dragon":
        dumped = any(e[:4] == ["Move", MASTER, 64, 16] and e[5] == 1 for e in events[index:end])
        destroyed = any(e[:4] == ["Move", target, 4, 16] and e[5] == 0 for e in events[index:end])
        master_target = next((e for e in events[end_of_chain:] if e[0] == "MasterTarget"), None)
        master_chain = next((i for i in range(end_of_chain, len(events))
                             if events[i][:3] == ["Chaining", MASTER, 1]), None)
        landed = dumped and destroyed and master_chain is not None and master_target is not None
        if landed:
            master_resolved, master_end = chain_resolution(events, master_chain)
            master_code, master_zone = master_target[1:]
            result["master_target"] = master_code
            result["master_target_zone"] = master_zone
            result["new_year_chained"] = any(e[:3] == ["Chaining", 25150, 0] for e in events[master_chain:master_end])
            result["master_target_moved"] = any(
                e[:4] == ["Move", master_code, 4, 8] and e[5:7] == [0, master_zone]
                for e in events[master_chain:master_end]
            )
            result["master_resolved"] = master_resolved
    else:
        landed = True
    return {**result, "landed": landed, **({} if landed else {"reason": "target outcome missing"})}


def interference_deck(scenario, main, seed):
    trap = HAND_TRAPS[scenario]
    opposing = list(main)
    if trap not in opposing:
        opposing[-1] = trap
    return {"main": fixed_deck(opposing, [trap], seed),
            "extra": DRAGON_EXTRA if scenario == "dragon" else [], "side": []}


def candidates(scenario, main, extra, seed):
    rng = random.Random(seed + list(HAND_TRAPS).index(scenario) * 1009)
    seen = set()
    while True:
        starter = rng.choice((36326160, 73810864))
        remaining = list(main)
        remaining.remove(starter)
        hand = [starter, *rng.sample(remaining, 4)]
        key = tuple(sorted(hand))
        if key in seen:
            continue
        seen.add(key)
        draw_seed = rng.randrange(1 << 31)
        yield {
            "scenario": scenario, "hand": hand, "draw_seed": draw_seed,
            "main": fixed_deck(main, hand, draw_seed), "extra": extra,
            "opponent": interference_deck(scenario, main, draw_seed),
        }


def play_variant(args, work, seat, ports, case, label, pressured):
    output = args.output
    suffix = "hit" if pressured else "clean"
    name = f"{label}-{suffix}-bot"
    (work / "opponent.json").write_text(json.dumps(case["opponent"]), encoding="utf-8")
    (work / "Decks" / "AI_Twins.ydk").write_text(
        "#main\n" + "\n".join(map(str, case["main"])) + "\n#extra\n"
        + "\n".join(map(str, case["extra"])) + "\n", encoding="utf-8",
    )
    try:
        result = play(work, output, name, seat, *ports,
                      interference=case["scenario"] if pressured else None)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        return {"error": str(error), "trace": name}
    result["trace"] = f"{name}.json"
    if pressured:
        result["interference"] = read_interference(output / result["trace"], case["scenario"])
    return result


def has_hit(variant):
    return variant.get("interference", {}).get("landed", False)


def comparison(case, reference, bot):
    same_interference = not has_hit(reference) or has_hit(bot)
    observed_gap = (round(reference["score"] - bot["score"], 3)
                    if "score" in bot and same_interference else None)
    target_score = max(reference["score"], reference.get("minimum_score", reference["score"]))
    return {"scenario": case["scenario"], "hand": case["hand"], "draw_seed": case["draw_seed"],
            "reference": reference, "bot": bot, "observed_gap": observed_gap,
            "target_score": target_score,
            "gap": None if observed_gap is None else round(target_score - bot["score"], 3)}


def run(args, work, ports, seat, main, extra):
    report, standard, attempts = [], [], []
    standard_path = args.output / "references.json"
    if args.reference_in:
        source = [(item["input"], item) for item in read_standard(args.reference_in, "interference")
                  if item["input"]["scenario"] in args.scenario]
        expected = len(source)
        engine = None
    elif args.inputs_in:
        items = json.loads(args.inputs_in.read_text(encoding="utf-8"))["cases"]
        source = []
        for item in items:
            case = item["input"]
            if case["scenario"] in args.scenario:
                target = item.get("minimum_scores", {}).get("clean")
                source.append(({**case, **({"clean_minimum_score": target} if target is not None else {})}, None))
        expected = len(source)
        engine = EngineReference(work, args.fixture, args.windbot, args.output, args.reference_ms)
    else:
        source = None
        expected = len(args.scenario) * args.samples
        engine = EngineReference(work, args.fixture, args.windbot, args.output, args.reference_ms)

    def evaluate(case, label, fixed=None):
        scenario = case["scenario"]
        if fixed:
            hit, clean = fixed["hit"], fixed["clean"]
        else:
            try:
                hit = engine.best(case, f"{label}-hit", scenario,
                                  qualify=lambda events: interference_outcome(events, scenario))
                if not has_hit(hit):
                    attempts.append({"scenario": scenario, "input": case, "reference": hit})
                    return False
                clean = engine.best(case, f"{label}-clean")
                if "score" not in clean:
                    attempts.append({"scenario": scenario, "input": case, "clean_reference": clean})
                    return False
            except (RuntimeError, subprocess.TimeoutExpired) as error:
                attempts.append({"scenario": scenario, "input": case, "error": str(error)})
                return False
            if "clean_minimum_score" in case:
                clean["minimum_score"] = case["clean_minimum_score"]

        bot_hit = play_variant(args, work, seat, ports, case, label, True)
        bot_clean = play_variant(args, work, seat, ports, case, label, False)
        standard.append({"input": case, "hit": hit, "clean": clean})
        report.append({"hit": comparison(case, hit, bot_hit), "clean": comparison(case, clean, bot_clean)})
        print(json.dumps({"scenario": scenario, "label": label,
                          "hit_gap": report[-1]["hit"]["gap"], "clean_gap": report[-1]["clean"]["gap"]}), flush=True)
        return True

    if source is not None:
        for i, (case, fixed) in enumerate(source):
            evaluate(case, f"{case['scenario']}-{i}", fixed)
    else:
        for scenario in args.scenario:
            accepted = 0
            for attempt, case in enumerate(candidates(scenario, main, extra, args.seed)):
                if accepted == args.samples or attempt >= args.attempts:
                    break
                accepted += evaluate(case, f"{scenario}-{attempt}")

    rows = [variant for pair in report for variant in pair.values()]
    errors = sum("error" in row["bot"] for row in rows)
    untargeted = sum(not has_hit(pair["hit"]["bot"]) for pair in report)
    gaps = [row["gap"] for row in rows if row["gap"] is not None]
    weak_references = sum(row["observed_gap"] is not None and row["observed_gap"] < 0 for row in rows)
    unmet_targets = sum(row["target_score"] > row["reference"]["score"] for row in rows)
    waits = [row["bot"].get("max_main_gap_ms") for row in rows if "score" in row["bot"]]
    mismatches = sum(row["bot"].get(key, 0) for row in rows for key in
                     ("resync", "choice_mismatches", "placement_mismatches"))
    counts = {name: sum(pair["hit"]["scenario"] == name for pair in report) for name in args.scenario}
    complete = len(report) == expected and expected > 0
    standard_ready = complete and not (weak_references or unmet_targets or errors)
    written = not args.reference_in and standard_ready
    if written:
        write_json(standard_path, {"version": 2, "mode": "interference", "reference_ms": args.reference_ms,
                                   "cases": standard})
    summary = {"pairs": len(report), "expected_pairs": expected, "by_scenario": counts,
               "invalid_attempts": len(attempts), "max_gap": max((max(0, gap) for gap in gaps), default=None),
               "weak_references": weak_references, "unmet_targets": unmet_targets,
               "standard_written": written,
               "max_main_gap_ms": max((ms for ms in waits if ms is not None), default=None),
               "reference_search_ms_max": max((row["reference"]["reference_search_ms"] for row in rows), default=None),
               "execution_mismatches": mismatches, "errors": errors, "missed_interference": untargeted}
    write_json(args.output / "report.json", {"summary": summary, "cases": report})
    if not args.reference_in:
        write_json(args.output / "attempts.json", attempts)
    print(json.dumps(summary), flush=True)
    failed = (not standard_ready or untargeted > 0 or mismatches > 0
              or any(gap > args.max_gap for gap in gaps)
              or (args.max_wait_ms is not None and any(ms is None or ms > args.max_wait_ms for ms in waits)))
    return int(failed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--windbot", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--table", type=Path)
    sources = parser.add_mutually_exclusive_group()
    sources.add_argument("--reference-in", type=Path, help="compare against a frozen independent answer set")
    sources.add_argument("--inputs-in", type=Path, help="explore fixed hands and decks from an existing case list")
    parser.add_argument("--scenario", choices=HAND_TRAPS, action="append")
    parser.add_argument("--seed", type=int, default=20261124)
    parser.add_argument("--samples", type=int, default=3, help="paired starting hands per scenario")
    parser.add_argument("--attempts", type=int, default=12, help="candidate hands per scenario")
    parser.add_argument("--reference-ms", type=int, default=10000)
    parser.add_argument("--max-gap", type=float, default=0.4)
    parser.add_argument("--max-wait-ms", type=int, help="optionally fail if the main-phase message gap exceeds this many milliseconds")
    args = parser.parse_args()
    args.scenario = args.scenario or list(HAND_TRAPS)
    if len(set(args.scenario)) != len(args.scenario):
        parser.error("choose each scenario at most once")
    if args.max_wait_ms is not None and args.max_wait_ms <= 0:
        parser.error("max-wait-ms must be positive")
    if not 0 < args.samples <= args.attempts or args.reference_ms <= 0:
        parser.error("invalid sample count or reference budget")
    args.output = args.output.resolve()
    input_path = args.reference_in or args.inputs_in
    if input_path and input_path.resolve() in (args.output / "report.json", args.output / "attempts.json"):
        parser.error("--output would overwrite the supplied input")
    if not args.reference_in and (args.output / "references.json").exists():
        parser.error("--output already contains a frozen answer set; use a new output directory")
    args.output.mkdir(parents=True, exist_ok=True)
    overlay = Path(__file__).resolve().parent.parent
    seat = overlay / "tests" / "twins-seat.cjs"
    main, extra = (overlay / "Decks" / "AI_Twins.ydk").read_text().split("#extra\n")
    main = list(map(int, main.split("#main\n")[1].splitlines()))
    extra = list(map(int, extra.split("!side")[0].splitlines()))
    with tempfile.TemporaryDirectory(prefix="twins-interference-") as directory:
        work = Path(directory)
        port, bot_port = available_port(), available_port()
        while bot_port == port:
            bot_port = available_port()
        prepare_fixture(work, args.fixture, args.windbot, overlay / "bots.json", port, bot_port)
        if args.table:
            shutil.copy2(args.table, work / "Decks" / "AI_Twins.plan.jsonl")
        with background(["node", "dist/index.js"], work, args.output / "server.log") as server:
            await_port(port, server)
            return run(args, work, (port, bot_port), seat, main, extra)


if __name__ == "__main__":
    raise SystemExit(main())
