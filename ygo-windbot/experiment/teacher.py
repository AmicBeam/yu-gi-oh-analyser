"""Capture local rule-mode responses after a settled first turn."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
import shutil
import tempfile

from compare_twins_interference import interference_outcome
from compare_twins_planner import available_port, await_port, background, play, prepare_fixture

HERE = Path(__file__).resolve().parent


@contextmanager
def teacher(fixture, windbot, output, *, mixed=False):
    path = output / "teacher"
    path.mkdir()
    with tempfile.TemporaryDirectory(prefix="twins-teacher-") as directory:
        work = Path(directory)
        port, bot_port = available_port(), available_port()
        while bot_port == port:
            bot_port = available_port()
        prepare_fixture(work, fixture, windbot, HERE.parent / "bots.json", port, bot_port)
        (work / "data").unlink()
        shutil.copytree(fixture / "data", work / "data")
        env = {key: value for key, value in os.environ.items() if not key.startswith("TWINS_")}
        startup = ["node", "--require", str(HERE / "capture-replay.cjs")]
        if mixed:
            startup.extend(["--require", str(HERE / "fixed-duel-seed.cjs")])
        with background([*startup, "dist/index.js"],
                        work, path / "server.log", env) as server:
            await_port(port, server)

            def capture(case, index, model=None, *, recovery_model=None):
                label = f"case-{index}"
                wire = work / "training-replay.json"
                wire.unlink(missing_ok=True)
                (work / "opponent.json").write_text(json.dumps(case["opponent"]))
                (work / "Decks" / "AI_Twins.ydk").write_text(
                    "#main\n" + "\n".join(map(str, case["main"])) + "\n#extra\n"
                    + "\n".join(map(str, case["extra"])) + "\n")
                settings = dict(env)
                if mixed:
                    (work / "duel-seed.json").write_text(json.dumps(case["duel_seed"]))
                    settings["TWINS_MIXED_PRESSURE"] = "hit" if case["opening_traps"] else "clean"
                if model:
                    settings["TWINS_PLAN_BRANCH_MODEL"] = str(model.resolve())
                if recovery_model:
                    settings["TWINS_PLAN_RECOVERY_MODEL"] = str(recovery_model.resolve(strict=True))
                scenario = case.get("scenario")
                observed = play(work, path, label, HERE.parent / "tests" / "twins-seat.cjs",
                                port, bot_port, interference=scenario, env=settings)
                if not wire.exists():
                    raise RuntimeError(f"{label}: no SRVPro2 replay captured in {path}")
                trace = json.loads((path / (label + ".json")).read_text())
                result = {**json.loads(wire.read_text()), "score": observed["score"], "board": observed["board"],
                          "traps": observed["traps"], "interference": interference_outcome(trace, scenario)
                          if scenario else None}
                if mixed:
                    result["trace"] = trace
                return result

            yield capture
