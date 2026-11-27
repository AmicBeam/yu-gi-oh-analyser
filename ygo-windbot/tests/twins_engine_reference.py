"""Run independent engine search and score only settled duel positions."""

import json
import os
from pathlib import Path
import subprocess


HERE = Path(__file__).resolve().parent


def write_json(path, value):
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    tmp.replace(path)


def read_standard(path, mode):
    standard = json.loads(path.read_text(encoding="utf-8"))
    cases = standard.get("cases")
    if standard.get("version") != 2 or standard.get("mode") != mode or not cases:
        raise ValueError(f"{path} is not an independently explored {mode} standard answer set")
    variants = ("hit", "clean") if mode == "interference" else ("reference",)
    for case in cases:
        for variant in variants:
            answer = case.get(variant, {})
            if answer.get("source") != "engine_search" or "score" not in answer:
                raise ValueError(f"{path} contains an unqualified {variant} answer")
            if "minimum_score" in answer and answer["score"] < answer["minimum_score"]:
                raise ValueError(f"{path} contains a {variant} answer below its known minimum")
    return cases


class EngineReference:
    def __init__(self, work, fixture, windbot, output, budget, *, scorer=None):
        self.work = work
        self.fixture = fixture.resolve()
        self.output = output
        self.budget = budget
        self.node_env = dict(os.environ, NODE_PATH=str(self.fixture / "node_modules"))
        paths = [str(windbot.resolve().parent), os.environ.get("MONO_PATH", "")]
        self.mono_env = dict(os.environ, MONO_PATH=os.pathsep.join(filter(None, paths)))
        self.scorer_launcher = [] if os.name == "nt" else ["mono"]
        if scorer is not None:
            self.scorer = scorer.resolve(strict=True)
        else:
            self.scorer = work / "TwinsEngineScore.exe"
            self.run(["mcs", "-nowarn:0649", f"-r:{windbot.resolve()}", "-r:System.Runtime.Serialization",
                      f"-out:{self.scorer}", str(HERE / "TwinsEngineScore.cs")], timeout=30)

    @staticmethod
    def run(command, *, timeout, env=None, script_input=None):
        result = subprocess.run(command, input=script_input, capture_output=True, text=True, env=env, timeout=timeout)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or result.stdout.strip())
        return result.stdout

    def engine(self, input, scenario, *, training_attempts=None, coach=None):
        config = {"input": input, "scenario": scenario, "budget": self.budget}
        if training_attempts is not None:
            config["trainingAttempts"] = training_attempts
        if coach is not None:
            config["coach"] = coach
        default_wasm = os.environ.get("DEFAULT_OCGCORE_WASM_PATH")
        if default_wasm:
            config["defaultWasm"] = str((self.work / default_wasm).resolve())
        config["scorer"] = [*self.scorer_launcher, str(self.scorer)]
        config_path = self.work / "engine-reference.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        result = self.run(["node", str(HERE / "twins-engine-reference.cjs"), str(self.fixture), str(config_path)],
                          timeout=self.budget / 1000 + 15,
                          env=dict(self.node_env, MONO_PATH=self.mono_env.get("MONO_PATH", "")))
        return json.loads(result)

    def score(self, candidates):
        observations = [{"position": c["position"], "nibiru_window": c.get("nibiru_window")} for c in candidates]
        scores = self.run([*self.scorer_launcher, str(self.scorer), str(self.fixture / "cards.cdb")], env=self.mono_env, timeout=30,
                          script_input="".join(json.dumps(value) + "\n" for value in observations))
        points = list(map(float, scores.splitlines()))
        if len(points) != len(candidates):
            raise RuntimeError("engine scorer returned an unexpected number of scores")
        return [round(point / 10, 3) for point in points]

    def best(self, input, label, scenario=None, qualify=None):
        result = self.engine(input, scenario)
        candidates = [
            (candidate, qualify(candidate["trace"]) if qualify else None)
            for candidate in result["candidates"]
        ]
        candidates = [(candidate, hit) for candidate, hit in candidates if hit is None or hit["landed"]]
        search_stats = {key: result[key] for key in ("reference_search_ms", "attempts", "unsupported_prompts")}
        if not candidates:
            return {"error": "engine search produced no qualifying settled board", "source": "engine_search",
                    **search_stats}
        points = self.score([candidate for candidate, _ in candidates])
        index = max(range(len(candidates)), key=points.__getitem__)
        route, hit = candidates[index]
        trace_file = self.output / f"{label}-engine-trace.json"
        trace_file.write_text(json.dumps(route["trace"]) + "\n", encoding="utf-8")
        p = route["position"]
        return {
            "source": "engine_search", "score": points[index], "board": p["board"],
            "traps": sum(bit for card, bit in ((98360333, 1), (25156, 2)) if card in p["set"]),
            "draws": p["draws"], "qualified_terminals": len(candidates),
            **search_stats, "trace": trace_file.name,
            **({"interference": hit} if hit is not None else {}),
        }
