#!/usr/bin/env python3
"""Run a paired live gate for one iteration and retest failing cases serially."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
from pathlib import Path
import re
import tempfile

from fit_iteration import read
from compare_twins_planner import available_port
from mixed_benchmark import average, run_group
from mixed_fixture import with_eradicator
from train import hand_key
from twins_engine_reference import write_json


def port_pairs(count):
    ports = set()
    while len(ports) < 2 * count:
        ports.add(available_port())
    numbers = sorted(ports)
    return list(zip(numbers[::2], numbers[1::2]))


def verdict(row, args):
    use = "hit" if row["opening_traps"] else "clean"
    old, new = (row["strategies"][name][use] for name in ("current", "candidate"))
    issues = [name + "_error" for name, value in (("current", old), ("candidate", new)) if "score" not in value]
    if issues:
        return issues
    row["delta"] = round(new["score"] - old["score"], 3)
    row["draw_delta"] = new["opponent_draws"] - old["opponent_draws"]
    if row["delta"] <= -args.max_drop:
        issues.append("score_drop")
    if row.get("regression"):
        if new["score"] < max(old["score"], row["minimum_score"]):
            issues.append("fixed_regression_score")
        if row["draw_delta"] > 0:
            issues.append("fixed_regression_more_draws")
    if args.max_wait_ms is not None and (new["max_main_gap_ms"] is None
            or new["max_main_gap_ms"] > args.max_wait_ms):
        issues.append("main_wait")
    if old["model_fallback"] or new["model_fallback"]:
        issues.append("model_fallback")
    if args.recovery_only:
        if old["recovery_model_fallback"] or new["recovery_model_fallback"]:
            issues.append("recovery_model_fallback")
        if new["recovery_frontiers"] and (not row["opening_traps"] or not new["confirmed_recovery"]):
            issues.append("recovery_without_confirmed_interference")
        if not row["opening_traps"] and row["delta"] != 0:
            issues.append("clean_score_changed")
    if any(new[key] > old[key] for key in ("resync", "choice_mismatches", "placement_mismatches")):
        issues.append("execution_mismatch")
    if row["delta"] == 0:
        a, b = old["priorities"], new["priorities"]
        if b["twin_searches"] > a["twin_searches"]:
            issues.append("equal_score_more_searches")
        if (a["photo_defense"] or a["requiem_defense"]) == (b["photo_defense"] or b["requiem_defense"]):
            if (b["photo_defense"], b["early_requiem"]) < (a["photo_defense"], a["early_requiem"]):
                issues.append("equal_score_weaker_nibiru_priority")
    return issues


def summarize(rows, args):
    for row in rows:
        row["issues"] = verdict(row, args)
    comparable = [row for row in rows if "delta" in row]
    pressured = [r for r in comparable if r["opening_traps"] and not r.get("regression")]
    hard = [r for r in pressured if all(r["strategies"][m]["hit"]["disruptions"] for m in ("current", "candidate"))]
    deltas = [r["delta"] for r in pressured]
    draws = [r["draw_delta"] for r in pressured]
    issues = Counter(issue for row in rows for issue in row["issues"])
    if not pressured or sum(deltas) < -1e-8:
        issues["mean_pressure_score"] += 1
    if sum(draws) > 0:
        issues["more_opponent_draws"] += 1
    frontiers = "recovery_frontiers" if args.recovery_only else "branch_frontiers"
    exercised = sum(any(v.get(frontiers, 0) > 0 for v in r["strategies"]["candidate"].values()) for r in rows)
    if not exercised:
        issues["model_not_exercised"] += 1
    if sum(deltas) <= 0 and sum(draws) >= 0:
        issues["no_pressure_gain"] += 1
    modes = ("current", "candidate")
    pressure_score = {m: average([r["strategies"][m]["hit"]["score"] for r in pressured]) for m in modes}
    max_wait = {m: max((value["max_main_gap_ms"] or 0 for row in comparable
                        for value in row["strategies"][m].values()), default=None) for m in modes}
    resumed = {m: sum(bool(r["strategies"][m]["hit"]["own_summons_after_last_hit"]
                           or r["strategies"][m]["hit"]["own_sets_after_last_hit"]) for r in hard) for m in modes}
    return {"cases": len(rows), "comparable": len(comparable), "pressured": len(pressured),
            "regression_cases": sum(bool(r.get("regression")) for r in rows),
            "both_hard_hit": len(hard), "both_hard_hit_mean_delta": average([r["delta"] for r in hard]),
            "resumed_after_hard_hit": resumed, "pressure_score": pressure_score, "max_wait_ms": max_wait,
            "improved": sum(d > 0 for d in deltas), "equal": deltas.count(0), "regressed": sum(d < 0 for d in deltas),
            "mean_pressure_delta": average(deltas), "mean_draw_delta": average(draws),
            "model_exercised": exercised, "issues": dict(issues), "gate_passed": not issues,
            "max_wait_gate_ms": args.max_wait_ms, "score_drop_gate": args.max_drop}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("inputs", "training-summary", "fixture", "windbot", "current", "candidate", "output"):
        p.add_argument(f"--{name}", required=True, type=Path)
    p.add_argument("--jobs", type=int, default=6)
    p.add_argument("--max-drop", type=float, default=0.5)
    p.add_argument("--max-wait-ms", type=int, help="optionally fail if the main-phase message gap exceeds this many milliseconds")
    p.add_argument("--recovery-only", action="store_true", help="use the incumbent in both arms and overlay the candidate only during recovery")
    p.add_argument("--current-recovery", type=Path, help="current recovery overlay for the control arm")
    p.add_argument("--regression-inputs", type=Path, help="fixed gates excluded from new-hand averages")
    args = p.parse_args()
    if args.current_recovery and not args.recovery_only:
        p.error("--current-recovery requires --recovery-only")
    cases = read(args.inputs)
    regressions = read(args.regression_inputs) if args.regression_inputs else []
    minimum_scores = {case["id"]: case["minimum_score"] for case in regressions}
    cases += regressions
    identifiers = [case["id"] for case in cases]
    if len(set(identifiers)) != len(cases) or any(not re.fullmatch(r"[a-z0-9-]+", x) for x in identifiers):
        p.error("evaluation requires unique safe case IDs")
    hands = [hand_key(case["hand"]) for case in cases]
    fit = read(args.training_summary)
    if not hands or len(set(hands)) != len(hands) or set(hands).intersection(map(tuple, fit["training_hands"])):
        p.error("evaluation requires distinct starting hands outside the fitted data")
    variants = {"current": args.current.resolve(strict=True), "candidate": args.candidate.resolve(strict=True)}
    if sha256(variants["candidate"].read_bytes()).hexdigest() != fit["candidate_sha256"] or (
        sha256(variants["current"].read_bytes()).hexdigest() != fit["incumbent_sha256"]
    ):
        p.error("the selected models do not match the iteration fit report")
    variant_env = None
    if args.recovery_only:
        variant_env = {"candidate": {"TWINS_PLAN_RECOVERY_MODEL": str(variants["candidate"])}}
        if args.current_recovery:
            variant_env["current"] = {"TWINS_PLAN_RECOVERY_MODEL": str(args.current_recovery.resolve(strict=True))}
        variants["candidate"] = variants["current"]
    if min(args.jobs, args.max_drop) <= 0 or (args.max_wait_ms is not None and args.max_wait_ms <= 0):
        p.error("jobs and gate thresholds must be positive")
    out = args.output.resolve()
    out.mkdir(parents=True)
    write_json(out / "inputs.json", cases)
    jobs = min(args.jobs, len(cases))
    with tempfile.TemporaryDirectory(prefix="twins-iteration-gate-") as directory:
        fixture, source = with_eradicator(args.fixture.resolve(), Path(directory) / "fixture")
        with ThreadPoolExecutor(max_workers=jobs) as pool:
            pending = [pool.submit(run_group, fixture, args.windbot, out, i, ports, cases[i::jobs],
                                   variants, clean_controls=False, variant_env=variant_env)
                       for i, ports in enumerate(port_pairs(jobs))]
            for worker in pending:
                worker.result()
        rows = [read(out / f'{case["id"]}-result.json') for case in cases]
        for row in rows:
            row["trace_directory"] = str(out)
            if row["id"] in minimum_scores:
                row.update(regression=True, minimum_score=minimum_scores[row["id"]])
        parallel = summarize(rows, args)
        write_json(out / "parallel-report.json", rows)
        blocking = {row["id"] for row in rows if row["issues"]}
        losses = [row for row in rows if row.get("delta", 0) < 0]
        if losses:
            blocking.add(min(losses, key=lambda row: row["delta"])["id"])
        if blocking:
            serial = out / "serial"
            serial.mkdir()
            run_group(fixture, args.windbot, serial, 0, port_pairs(1)[0], [c for c in cases if c["id"] in blocking],
                      variants, clean_controls=False, variant_env=variant_env)
            rows = [{**read(serial / f'{row["id"]}-result.json'), "trace_directory": str(serial)}
                    if row["id"] in blocking else row for row in rows]
            for row in rows:
                if row["id"] in minimum_scores:
                    row.update(regression=True, minimum_score=minimum_scores[row["id"]])
    final = summarize(rows, args)
    final.update(serial_rechecks=sorted(blocking), fixture_eradicator=source, parallel=parallel,
                 recovery_only=args.recovery_only)
    write_json(out / "report.json", rows)
    write_json(out / "summary.json", final)
    print(final, flush=True)
    return int(not final["gate_passed"])


if __name__ == "__main__":
    raise SystemExit(main())
