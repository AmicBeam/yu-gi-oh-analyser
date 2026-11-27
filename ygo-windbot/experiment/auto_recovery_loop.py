#!/usr/bin/env python3
"""Run a bounded, resumable recovery-model experiment with Windows offline search."""

import argparse
import base64
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tarfile
from time import monotonic

from fit_iteration import read
from mixed_benchmark import HERE, make_inputs
from train import MODEL_FILE, hand_key
from twins_engine_reference import write_json


class Budget:
    def __init__(self, seconds):
        self.end = monotonic() + seconds
        self.started = datetime.now(timezone.utc)
        self.deadline = self.started + timedelta(seconds=seconds)

    def check(self):
        remaining = self.end - monotonic()
        if remaining <= 0:
            raise TimeoutError("loop time budget reached")
        return remaining

    def run(self, command, *, allowed=(0,)):
        remaining = self.check()
        with subprocess.Popen(list(map(str, command)), start_new_session=True) as process:
            try:
                status = process.wait(timeout=remaining)
            except (subprocess.TimeoutExpired, KeyboardInterrupt):
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                raise
        if status not in allowed:
            raise subprocess.CalledProcessError(status, command)
        return status


def ps_quote(value):
    return "'" + str(value).replace("'", "''") + "'"


class Windows:
    def __init__(self, args, budget):
        self.target = args.ssh_target
        self.root = args.windows_root.replace("\\", "/").rstrip("/")
        self.budget = budget
        self.connection = ["-i", args.ssh_identity, "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes",
                           "-o", "ConnectTimeout=8", "-o", "StrictHostKeyChecking=yes",
                           "-o", "HostKeyAlgorithms=ssh-ed25519"]

    def shell(self, script):
        prefix = "$ErrorActionPreference='Stop'\n$ProgressPreference='SilentlyContinue'\n$env:PYTHONUTF8='1'\n"
        encoded = base64.b64encode((prefix + script).encode("utf-16le")).decode("ascii")
        status = self.budget.run(["ssh", *self.connection, self.target,
                                  "powershell.exe -NoProfile -EncodedCommand " + encoded], allowed=(0, 124))
        if status == 124:
            raise TimeoutError("Windows training reached the time limit")

    def upload(self, source, remote):
        self.budget.run(["scp", *self.connection, source, f"{self.target}:{self.root}/{remote}"])

    def download(self, remote, target):
        self.budget.run(["scp", *self.connection, f"{self.target}:{self.root}/{remote}", target])


def digest(file):
    return sha256(file.read_bytes()).hexdigest()


def fresh_cases(seed, seen, clean_count, prefix):
    # Require access to a direct starter or Twins search spell in this tiny smoke batch.
    access = {36326160, 73810864, 82699999, 37582948, 61976639}
    clean, pressure = [], {}
    for case in make_inputs(seed, 32, [], seen):
        if not access.intersection(case["hand"]):
            continue
        count = len(case["opening_traps"])
        if not count and len(clean) < clean_count:
            clean.append(case)
        elif count and count not in pressure:
            pressure[count] = case
    if len(clean) != clean_count or len(pressure) != 5:
        raise RuntimeError("could not generate the required new cases")
    selected = clean + list(pressure.values())
    for case in selected:
        case["id"] = prefix + case["id"]
    return selected


def keep_partial(path):
    if not path.exists():
        return
    index = 1
    while path.with_name(f"{path.name}-interrupted-{index}").exists():
        index += 1
    path.rename(path.with_name(f"{path.name}-interrupted-{index}"))


def setup_windows(remote, out, job):
    remote.shell(f"""
$run=Join-Path {ps_quote(remote.root)} {ps_quote(job)}
New-Item -ItemType Directory -Force -Path $run | Out-Null
""")
    bundle = out / "code.tar.gz"
    if not bundle.exists():
        with tarfile.open(bundle.with_suffix(".tmp"), "w:gz") as archive:
            root = HERE.parent
            for path in [root / "Decks", root / "bots.json", HERE, root / "tests"]:
                archive.add(path, arcname=str(Path("ygo-windbot") / path.relative_to(root)),
                            filter=lambda info: None if "__pycache__" in info.name else info)
        bundle.with_suffix(".tmp").replace(bundle)
    remote.upload(bundle, job + "/code.tar.gz")
    remote.shell(f"""
$run=Join-Path {ps_quote(remote.root)} {ps_quote(job)}
if (-not (Test-Path (Join-Path $run 'code/ygo-windbot/experiment/recovery_pilot.py'))) {{
  $code=New-Item -ItemType Directory -Force -Path (Join-Path $run 'code')
  tar.exe -xzf (Join-Path $run 'code.tar.gz') -C $code.FullName
  if ($LASTEXITCODE -ne 0) {{exit $LASTEXITCODE}}
}}
""")


def search_and_fit(remote, args, job, folder, old_searches, incumbent):
    path = f"{job}/{folder.name}"
    remote.shell(f"New-Item -ItemType Directory -Force -Path (Join-Path {ps_quote(remote.root)} {ps_quote(path)}) | Out-Null")
    source = folder / "capture" / "guides.json"
    remote.upload(source, path + "/guides.json")
    prior = args.windows_prior
    supplements = [args.windows_clean_search, *old_searches, path + "/search"]
    script_dir = job + "/code/ygo-windbot/experiment"
    search_cmd = subprocess.list2cmdline([
        script_dir + "/recovery_pilot.py", "--guides", path + "/guides.json", "--fixture", "workspace/fixture",
        "--windbot", "workspace/scorer/WindBot.exe", "--scorer", "workspace/scorer/TwinsEngineScore.exe",
        "--output", path + "/search", "--jobs", str(args.jobs), "--attempts", str(args.attempts),
        "--reference-ms", str(args.reference_ms), "--quarantine-divergence",
    ])
    fit_args = [script_dir + "/fit_iteration.py", "--previous", prior, "--fresh", args.windows_mixed_search,
                "--fresh-guides", args.windows_mixed_guides]
    for stage in supplements:
        fit_args.extend(["--supplement", stage])
    fit_args.extend(["--incumbent", prior + "/" + MODEL_FILE, "--output", path + "/fit"])
    fit_cmd = subprocess.list2cmdline(fit_args)
    # Give the Windows watchdog time to terminate the child tree before SSH's deadline.
    deadline_ms = int((remote.budget.deadline - timedelta(seconds=5)).timestamp() * 1000)
    remote.shell(f"""
Set-Location {ps_quote(remote.root)}
function Invoke-TrainingPython([string]$CommandLine) {{
  $remaining = {deadline_ms} - [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
  if ($remaining -le 0) {{exit 124}}
  $python = (Get-Command python.exe).Source
  $process = Start-Process -FilePath $python -ArgumentList $CommandLine -NoNewWindow -PassThru
  $null = $process.Handle
  if (-not $process.WaitForExit([int]$remaining)) {{
    taskkill.exe /PID $process.Id /T /F | Out-Null
    exit 124
  }}
  if ($null -eq $process.ExitCode) {{throw 'Could not read the training process exit code'}}
  if ($process.ExitCode -ne 0) {{exit $process.ExitCode}}
}}
$search = {ps_quote(path + '/search')}
$fit = {ps_quote(path + '/fit')}
$guide = {ps_quote(path + '/guides.json')}
$expected = {ps_quote(digest(source))}
if ((Get-FileHash -Algorithm SHA256 $guide).Hash -ne $expected) {{throw 'Guide transfer hash mismatch'}}
$manifest = Join-Path $search 'manifest.json'
if (Test-Path $manifest) {{
  if ((Get-Content -Raw $manifest | ConvertFrom-Json).guides_sha256 -ne $expected) {{
    throw 'Saved Windows search used different guides'
  }}
}} elseif (Test-Path (Join-Path $fit 'summary.json')) {{
  throw 'Saved fit has no search manifest'
}}
if (-not (Test-Path (Join-Path $fit 'summary.json'))) {{
  Invoke-TrainingPython {ps_quote(search_cmd)}
  if (Test-Path $fit) {{Move-Item -LiteralPath $fit -Destination ($fit+'-interrupted-'+[Guid]::NewGuid().ToString('N'))}}
  Invoke-TrainingPython {ps_quote(fit_cmd)}
}}
""")
    local_fit = folder / "fit"
    local_fit.mkdir(exist_ok=True)
    for name in ("summary.json", MODEL_FILE):
        remote.download(path + "/fit/" + name, local_fit / name)
    fit = read(local_fit / "summary.json")
    if fit["incumbent_sha256"] != digest(incumbent) or fit["candidate_sha256"] != digest(local_fit / MODEL_FILE):
        raise RuntimeError("Windows model hashes failed verification")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ("fixture", "windbot", "incumbent", "output", "ssh-identity"):
        p.add_argument(f"--{name}", type=Path, required=True)
    p.add_argument("--ssh-target", required=True)
    p.add_argument("--windows-root", required=True)
    p.add_argument("--windows-prior", default="runs/pilot-anchored")
    p.add_argument("--windows-mixed-search", default="runs/iteration-01/search")
    p.add_argument("--windows-mixed-guides", default="inputs/iteration-01-guides.json")
    p.add_argument("--windows-clean-search", default="runs/clean-balance-01/search")
    p.add_argument("--windows-supplemental-search", action="append", default=[])
    p.add_argument("--job", required=True)
    p.add_argument("--exclude", type=Path, action="append", default=[])
    p.add_argument("--regression-inputs", type=Path)
    p.add_argument("--rounds", type=int, default=2)
    p.add_argument("--jobs", type=int, default=5)
    p.add_argument("--attempts", type=int, default=100)
    p.add_argument("--reference-ms", type=int, default=8000)
    p.add_argument("--max-seconds", type=int, default=900)
    p.add_argument("--seed", type=int, default=202611277)
    args = p.parse_args()
    if min(args.rounds, args.jobs, args.attempts, args.reference_ms, args.max_seconds) < 1:
        p.error("rounds, workers and budgets must be positive")
    if not re.fullmatch(r"[a-z0-9-]+", args.job):
        p.error("job must contain only lowercase letters, digits and hyphens")
    args.output.mkdir(parents=True, exist_ok=True)
    out = args.output.resolve()
    tracked = ("fixture", "windbot", "incumbent", "ssh_identity", "ssh_target", "windows_root", "windows_prior",
               "windows_mixed_search", "windows_mixed_guides", "windows_clean_search", "job", "rounds",
               "jobs", "attempts", "reference_ms", "seed")
    contract = {field: str(getattr(args, field)) for field in tracked}
    contract["incumbent_sha256"] = digest(args.incumbent)
    contract["windbot_sha256"] = digest(args.windbot)
    contract["exclude_sha256"] = [digest(file) for file in args.exclude]
    if args.windows_supplemental_search:
        contract["supplemental_search"] = args.windows_supplemental_search
    if args.regression_inputs:
        contract["regression_sha256"] = digest(args.regression_inputs)
    config = out / "contract.json"
    if config.exists() and read(config) != contract:
        p.error("resume options or input files differ from the saved run")
    if not config.exists():
        write_json(config, contract)
    budget = Budget(args.max_seconds)
    status_file = out / "run-status.json"
    status = {"pid": os.getpid(), "started_utc": budget.started.isoformat(), "deadline_utc": budget.deadline.isoformat()}
    write_json(status_file, {**status, "status": "running"})
    try:
        run_rounds(args, out, budget)
    except (TimeoutError, subprocess.TimeoutExpired):
        state = "time_limit"
        print("Time limit reached; keep finished rounds and search checkpoints", flush=True)
    except (Exception, KeyboardInterrupt):
        write_json(status_file, {**status, "status": "interrupted"})
        raise
    else:
        state = "completed"
    write_json(status_file, {**status, "status": state})


def run_rounds(args, out, budget):
    exclusions = [*args.exclude, *([args.regression_inputs] if args.regression_inputs else [])]
    seen = {hand_key(case["hand"]) for path in exclusions for case in read(path)}
    remote = Windows(args, budget)
    job = f"runs/{args.job}"
    current_recovery = None
    previous_searches, rounds = list(args.windows_supplemental_search), []
    setup_done = False
    for index in range(1, args.rounds + 1):
        budget.check()
        folder = out / f"round-{index:02d}"
        folder.mkdir(exist_ok=True)
        freeze = folder / "frozen-inputs.json"
        if not freeze.exists():
            train = fresh_cases(args.seed + index * 2, seen, 4, f"r{index}-t-")
            seen.update(hand_key(case["hand"]) for case in train)
            held = fresh_cases(args.seed + index * 2 + 1, seen, 2, f"r{index}-e-")
            write_json(freeze, {"train": train, "held": held})
        frozen = read(freeze)
        seen.update(hand_key(case["hand"]) for case in frozen["train"] + frozen["held"])
        write_json(folder / "train.json", frozen["train"])
        write_json(folder / "held.json", frozen["held"])
        record_path = folder / "decision.json"
        if not record_path.exists():
            print(f"Round {index}: {len(frozen['train'])} new training hands and {len(frozen['held'])} frozen evaluation hands",
                  flush=True)
            capture = folder / "capture"
            if not (capture / "guides.json").exists():
                keep_partial(capture)
                command = [sys.executable, HERE / "capture_mixed_guides.py", "--fixture", args.fixture,
                           "--windbot", args.windbot, "--model", args.incumbent, "--inputs-in", folder / "train.json",
                           "--output", capture, "--jobs", args.jobs]
                if current_recovery:
                    command.extend(["--recovery-model", current_recovery])
                try:
                    budget.run(command)
                except subprocess.CalledProcessError:
                    keep_partial(capture)
                    print(f"Round {index}: retrying live capture with the same frozen hands", flush=True)
                    budget.run(command)
            if not (folder / "fit" / "summary.json").exists():
                if not setup_done:
                    setup_windows(remote, out, job)
                    setup_done = True
                search_and_fit(remote, args, job, folder, previous_searches, args.incumbent)
            gate = folder / "gate"
            if not (gate / "summary.json").exists() or not (gate / "report.json").exists():
                keep_partial(gate)
                command = [sys.executable, HERE / "iteration_gate.py", "--inputs", folder / "held.json",
                           "--training-summary", folder / "fit" / "summary.json", "--fixture", args.fixture,
                           "--windbot", args.windbot, "--current", args.incumbent, "--candidate",
                           folder / "fit" / MODEL_FILE, "--output", gate, "--jobs", args.jobs, "--recovery-only"]
                if current_recovery:
                    command.extend(["--current-recovery", current_recovery])
                if args.regression_inputs:
                    command.extend(["--regression-inputs", args.regression_inputs])
                budget.run(command, allowed=(0, 1))
            result = read(gate / "summary.json")
            fit = read(folder / "fit" / "summary.json")
            accepted = result["gate_passed"]
            write_json(record_path, {"round": index, "search_location": f"{job}/{folder.name}/search",
                                    "prior_recovery": digest(current_recovery) if current_recovery else None,
                                    "candidate_sha256": fit["candidate_sha256"], "promoted": accepted,
                                    "cases": result["cases"], "comparable": result["comparable"],
                                    "new_pairs_total": fit["new_pairs"], "issues": result["issues"],
                                    "mean_pressure_delta": result["mean_pressure_delta"]})
        decision = read(record_path)
        if decision["prior_recovery"] != (digest(current_recovery) if current_recovery else None):
            raise RuntimeError("saved checkpoint used a different recovery incumbent")
        if decision["promoted"]:
            current_recovery = folder / "fit" / MODEL_FILE
            if digest(current_recovery) != decision["candidate_sha256"]:
                raise RuntimeError("promoted model hash changed after the gate")
        rounds.append(decision)
        previous_searches.append(decision["search_location"])
        write_json(out / "summary.json", {"completed": len(rounds), "requested": args.rounds, "rounds": rounds,
                                        "active_recovery_model": str(current_recovery) if current_recovery else None})
        print(f"Round {index}: {'promoted experimental recovery overlay' if decision['promoted'] else 'kept prior policy'}",
              flush=True)


if __name__ == "__main__":
    main()
