#!/usr/bin/env python3
"""Replay fixed SRVPro2 duels in rule and learned modes with the opening table off."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from train import HERE, hand_key, read_deck
from compare_twins_interference import interference_deck, read_interference
from compare_twins_planner import ASH, available_port, await_port, background, fixed_deck, play, prepare_fixture
from twins_engine_reference import write_json

TESTS = HERE.parent / "tests"
MODEL_ENV = "TWINS_PLAN_BRANCH_MODEL"
PRIORITIES = ("twin_searches", "photo_defense", "requiem_defense", "early_requiem", "password_banished", "photo_used")
CAESAR, YAMA = 79559912, 24269961


def cases(path):
    main, extra = read_deck()
    interference = json.loads((TESTS / "fixtures/twins-interference-inputs.json").read_text())["cases"]
    for row in json.loads(path.read_text()):
        if "interference_input" in row:
            source = interference[row["interference_input"]]["input"]
            if hand_key(source["hand"]) != hand_key(row["hand"]):
                raise ValueError(f'{row["id"]}: fixture opening hand changed')
            for scenario in (None, source["scenario"]):
                yield {**source, "id": row["id"] + ("" if scenario else "-clean"), "scenario": scenario}
        elif row.get("scenario"):
            scenario, seed = row["scenario"], row["seed"]
            source = {**row, "main": fixed_deck(main, row["hand"], seed), "extra": extra,
                      "opponent": interference_deck(scenario, main, seed)}
            yield {**source, "id": row["id"] + "-clean", "scenario": None}
            yield source
        else:
            seed = row["seed"]
            ash = row.get("ash_target", 0)
            yield {**row, "main": fixed_deck(main, row["hand"], seed + row["case"]), "extra": extra,
                   "opponent": {"main": fixed_deck(main, [ASH], seed) if ash else main, "extra": []}}


def timing_priorities(trace):
    turns = [i for i, event in enumerate(trace) if event[0] == "NewTurn"]
    first_turn = trace[turns[0]:turns[1]]
    return {
        "caesar_first_event": next((i for i, e in enumerate(first_turn)
            if e[:4] == ["Move", CAESAR, 64, 4] and e[7] == 0), None),
        "yama_grave_first_event": next((i for i, e in enumerate(first_turn)
            if e[:2] == ["Move", YAMA] and e[3] == 16 and e[7] == 0), None),
    }


def run_variant(work, out, label, ports, case, env):
    scenario = case.get("scenario")
    try:
        result = play(work, out, label, TESTS / "twins-seat.cjs", *ports, ash_target=case.get("ash_target", 0),
                      interference=scenario, env=env)
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        if env.get("TWINS_MIXED_PRESSURE"):
            (out / f"{label}.error.txt").write_text(str(error) + "\n")
        return {"error": str(error).split("\n", 1)[0] or type(error).__name__, "log": label + ".log"}

    trace_path = out / (label + ".json")
    trace = json.loads(trace_path.read_text())
    log = (out / (label + ".log")).read_text().rsplit("Duel started:", 1)[-1]
    priorities = re.findall(r"TwinsPlan observed-priorities ([\d ]+)", log)
    frontiers = re.findall(r"TwinsPlan branch-frontiers (\d+)", log)
    recovery_frontiers = re.findall(r"TwinsPlan recovery-frontiers (\d+)", log)
    if not priorities or not frontiers:
        return {"error": "missing experiment trace; use the experiment WindBot build", "log": label + ".log"}
    result["priorities"] = dict(zip(PRIORITIES, map(int, priorities[-1].split()), strict=True))
    result["branch_frontiers"] = int(frontiers[-1])
    result["recovery_frontiers"] = int(recovery_frontiers[-1]) if recovery_frontiers else 0
    result["confirmed_recovery"] = re.findall(r"TwinsPlan recovery-confirmed (\S+)", log)
    result["recovery_model_fallback"] = "TwinsPlan recovery-model fallback" in log
    result["model_loaded"] = "TwinsPlan branch-model loaded" in log
    result["model_fallback"] = "TwinsPlan branch-model fallback" in log
    result["trace"] = trace_path.name
    result["priorities"].update(timing_priorities(trace))
    result["priorities"]["challenge_set"] = bool(result["traps"] & 2)
    if scenario:
        result["interference"] = read_interference(trace_path, scenario)
    return result


def compare(row, max_wait):
    rule, learned = (row[mode] for mode in ("rule", "learned"))
    issues = [f"{mode}_error" for mode in ("rule", "learned") if "error" in row[mode]]
    if issues:
        return issues
    if rule["table_hits"] or learned["table_hits"] or rule["branch_frontiers"] or rule["model_loaded"]:
        issues.append("invalid_control")
    if learned["model_fallback"] or (learned["branch_frontiers"] and not learned["model_loaded"]):
        issues.append("model_load_failed")
    if any(learned[key] > rule[key] for key in ("resync", "choice_mismatches", "placement_mismatches")):
        issues.append("execution_mismatch")
    if max_wait is not None and (learned["max_main_gap_ms"] is None or learned["max_main_gap_ms"] > max_wait):
        issues.append("main_wait")
    if row.get("scenario"):
        outcomes = [r["interference"]["landed"] for r in (rule, learned)]
        if not all(outcomes):
            issues.append("interference_outcome_differs" if any(outcomes) else "interference_not_exercised")
            return issues
    row["delta"] = round(learned["score"] - rule["score"], 3)
    if row["delta"] < 0:
        issues.append("score_regression")
    if row["delta"] == 0:
        # Record the observed tie-break inputs that differ, without inferring the planner's
        # internal action indexes from a server trace.
        row["equal_score_priority_changes"] = {
            key: [rule["priorities"][key], learned["priorities"][key]]
            for key in PRIORITIES + ("challenge_set",)
            if rule["priorities"][key] != learned["priorities"][key]
        }
        a, b = rule["priorities"], learned["priorities"]
        if a["twin_searches"] < b["twin_searches"]:
            issues.append("more_twin_searches_at_equal_score")
        if (a["photo_defense"] or a["requiem_defense"]) == (b["photo_defense"] or b["requiem_defense"]):
            if (b["photo_defense"], b["early_requiem"]) < (a["photo_defense"], a["early_requiem"]):
                issues.append("weaker_nibiru_tiebreak")
    return issues


def run_shard(args, out, ports, cases, variants, shard):
    with tempfile.TemporaryDirectory(prefix="twins-model-ab-") as directory:
        work = Path(directory)
        port, bot_port = ports
        prepare_fixture(work, args.fixture, args.windbot, HERE.parent / "bots.json", port, bot_port)
        # The server may maintain local data files during the run.
        (work / "data").unlink()
        shutil.copytree(args.fixture / "data", work / "data")
        with background(["node", "dist/index.js"], work, out / f"server-{shard}.log", variants["rule"]) as server:
            await_port(port, server)
            for case in cases:
                row = {key: case[key] for key in ("id", "hand", "ash_target", "scenario") if key in case}
                (work / "opponent.json").write_text(json.dumps(case["opponent"]))
                (work / "Decks" / "AI_Twins.ydk").write_text(
                    "#main\n" + "\n".join(map(str, case["main"])) + "\n#extra\n"
                    + "\n".join(map(str, case["extra"])) + "\n")
                for mode, settings in variants.items():
                    row[mode] = run_variant(work, out, f'{case["id"]}-{mode}', ports, case, settings)
                row["issues"] = compare(row, args.max_wait_ms)
                write_json(out / f'{case["id"]}-result.json', row)
                print(row["id"], *(row[m].get("score") for m in variants), row["issues"], flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--windbot", type=Path, required=True, help="experiment build used with and without the model")
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--training-inputs", type=Path, required=True)
    parser.add_argument("--holdout", type=Path, action="append", help="fixed case list; repeat to combine suites")
    parser.add_argument("--output", type=Path, required=True, help="new output directory")
    parser.add_argument("--max-wait-ms", type=int, help="optionally fail if the main-phase message gap exceeds this many milliseconds")
    parser.add_argument("--jobs", type=int, default=1, help="independent local fixtures; keep one for serial timing")
    parser.add_argument("--case", action="append", help="replay only the named case, repeatable")
    args = parser.parse_args()
    model = args.model.resolve(strict=True)
    held = [case for path in args.holdout or [HERE / "holdout.json"] for case in cases(path)]
    if args.case:
        if set(args.case) - {case["id"] for case in held}:
            parser.error("--case contains an unknown ID")
        held = [case for case in held if case["id"] in args.case]
    if min(args.jobs, len(held)) <= 0 or (args.max_wait_ms is not None and args.max_wait_ms <= 0):
        parser.error("jobs, max-wait-ms and number of held-out cases must be positive")
    if len({row["id"] for row in held}) != len(held) or any(
        not re.fullmatch(r"[a-z0-9-]+", row["id"]) for row in held
    ):
        parser.error("holdout IDs must be unique and contain only lowercase letters, numbers and hyphens")
    trained = {hand_key(row["hand"]) for row in json.loads(args.training_inputs.read_text())}
    held_hands = {hand_key(row["hand"]) for row in held}
    if trained & held_hands:
        parser.error("training and holdout contain the same opening hand")
    args.output.mkdir(parents=True)
    out = args.output.resolve()
    write_json(out / "holdout-inputs.json", held)
    env = {name: value for name, value in os.environ.items()
           if not name.startswith("TWINS_")}
    variants = {"rule": env, "learned": {**env, MODEL_ENV: str(model)}}
    unique, replay_ids = {}, {}
    for case in held:
        replay = (tuple(case["main"]), tuple(case["extra"]), json.dumps(case["opponent"], sort_keys=True),
                  case.get("ash_target"), case.get("scenario"))
        replay_ids[case["id"]] = unique.setdefault(replay, case)["id"]
    replays = list(unique.values())
    jobs = min(args.jobs, len(replays))
    ports = set()
    while len(ports) < 2 * jobs:
        ports.add(available_port())
    port_pairs = iter(sorted(ports))
    with ThreadPoolExecutor(max_workers=jobs) as pool:
        pending = [pool.submit(run_shard, args, out, pair, replays[shard::jobs], variants, shard)
                   for shard, pair in enumerate(zip(port_pairs, port_pairs))]
        for shard in pending:
            shard.result()

    report = []
    for case in held:
        replay_id = replay_ids[case["id"]]
        row = json.loads((out / f"{replay_id}-result.json").read_text())
        if replay_id != case["id"]:
            row.update(id=case["id"], same_replay_as=replay_id)
        report.append(row)
    write_json(out / "report.json", report)
    deltas = [row["delta"] for row in report if "delta" in row]
    summary = {
        "cases": len(report), "unique_replays": len(replays), "jobs": jobs, "holdout_unique_hands": len(held_hands),
        "training_unique_hands": len(trained), "training_holdout_overlap": 0,
        "comparable": len(deltas), "improved": sum(d > 0 for d in deltas),
        "equal": deltas.count(0), "regressed": sum(d < 0 for d in deltas),
        "learned_search_exercised": sum(row["learned"].get("branch_frontiers", 0) > 0 for row in report),
        "max_main_gap_ms": {mode: max((row[mode].get("max_main_gap_ms") or 0 for row in report), default=0)
                            for mode in variants},
        "max_search_ms": {mode: max((row[mode].get("max_search_ms", 0) for row in report), default=0)
                          for mode in variants},
        "max_wait_gate_ms": args.max_wait_ms,
        "issues": dict(Counter(issue for row in report for issue in row["issues"])),
        "failed_cases": [row["id"] for row in report if row["issues"]],
        "model_sha256": hashlib.sha256(model.read_bytes()).hexdigest(),
    }
    write_json(out / "summary.json", summary)
    print(json.dumps(summary), flush=True)
    return int(bool(summary["failed_cases"] or not summary["learned_search_exercised"]))


if __name__ == "__main__":
    raise SystemExit(main())
