#!/usr/bin/env python3
"""Train a separate action ranker on going-second OCGCore rollouts."""

import argparse
from collections import Counter
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from datetime import datetime, timedelta, timezone
from hashlib import sha256
import json
import math
import os
from pathlib import Path
import random
import subprocess
import tempfile
from time import monotonic

from train import HERE, hand_key, read_deck
from compare_twins_planner import fixed_deck
from twins_engine_reference import write_json

TRAPS = (14558128, 94145022, 27204311, 59438930, 10045474, 73642296, 24508238, 33854624)
FILL = (46986414, 74677422, 31305911, 7572887, 89631139, 33396948, 53129443,
        99785935, 76812113, 15025844, 33420078, 28279543, 55291359, 40640057)
SET_CARDS = ((1, 98360333), (2, 25156), (4, 65681983), (8, 10045474))
ALIASES = {9205573: (9205573, 9205574), 36609518: (36609518, 36609519)}
STARTERS = {36326160, 73810864, 82699999, 37582948, 61976639}


def make_case(boards, index, seed, main, extra, seen):
    rng = random.Random(seed + index * 397)
    hand = rng.sample(main, 5)
    while not STARTERS.intersection(hand) or hand_key(hand) in seen:
        hand = rng.sample(main, 5)
    seen.add(hand_key(hand))
    # Cover lethal windows as well as full opposing boards. The layouts still
    # come from recorded matches; low-LP cases represent an existing duel state.
    reduced = [row for row in boards if sum(bool(code) for code in row["board"]) + row["traps"].bit_count() <= 2]
    short_clock = index % 4 == 0
    row = rng.choice(reduced if short_clock and reduced else boards)
    enemy = []
    enemy_extra = list(extra)
    enemy_main = [card for card in FILL for _ in range(3)]
    for slot, code in enumerate(row["board"]):
        if not code:
            continue
        enemy.append({"location": 4, "sequence": slot, "code": code, "position": 1})
        removed = next((id for id in ALIASES.get(code, (code,)) if id in enemy_extra), None)
        if removed is not None:
            enemy_extra.remove(removed)
    for slot, (_, id) in enumerate((pair for pair in SET_CARDS if row["traps"] & pair[0])):
        enemy.append({"location": 8, "sequence": slot, "code": id, "position": 8})
    hidden = rng.sample(TRAPS, index % 3)
    rng.shuffle(enemy_main)
    opp_lp = rng.choice((1200, 1800, 2800)) if short_clock else rng.choice((2800, 4000, 5500, 6500, 8000, 8000))
    return {"id": index, "seed": rng.randrange(1 << 31), "hand": hand, "main": fixed_deck(main, hand, seed+index),
            "extra": extra, "field": enemy, "opponent_lp": opp_lp,
            "opponent": {"main": hidden + enemy_main[:40-len(hidden)], "extra": enemy_extra}}


def run_case(case, args, deadline):
    remaining = deadline - monotonic()
    if remaining < 4:
        return None
    budget = min(args.search_ms, int(max(1000, (remaining - 2) * 1000)))
    scorer = [str(args.scorer.resolve())] if os.name == "nt" else ["mono", str(args.scorer.resolve())]
    with tempfile.TemporaryDirectory(prefix="twins-second-") as work:
        config = Path(work) / "case.json"
        write_json(config, {"input": case, "attempts": args.attempts, "budget": budget, "scorer": scorer})
        env = {**os.environ, "NODE_PATH": str(args.fixture.resolve() / "node_modules")}
        result = subprocess.run(
            ["node", str(HERE / "second-turn-search.cjs"), str(args.fixture.resolve()), str(config)],
            capture_output=True, env=env, timeout=min(remaining, budget/1000+15),
        )
        if result.returncode:
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace") or f"engine exited {result.returncode}")
    data = json.loads(result.stdout)
    pairs = data.pop("pairs")
    if len(pairs) > 192:
        rng = random.Random(case["seed"])
        pairs = rng.sample(pairs, 192)
    return {**data, "case": case, "pairs": pairs, "heldout": case["id"] % 5 == 0}


def difference(pair):
    d = Counter(pair["high"])
    d.subtract(pair["low"])
    return [(key, value) for key, value in d.items() if value]


def update(weights, pairs):
    for pair in pairs:
        d = difference(pair)
        margin = sum(weights[key] * value for key, value in d)
        step = 0.035 * (1.0 + 0.15 * (3 - pair["axis"]))
        gradient = step / (1 + math.exp(max(-30, min(30, margin))))
        for key,value in d:
            weights[key] += gradient * value


def report(output, counts, weights, heldout, status, deadline):
    correct = sum(sum(weights[key]*value for key,value in difference(pair)) > 0 for pair in heldout)
    summary = {**counts, "status": status, "evaluation_pairs": len(heldout),
               "evaluation_accuracy": round(correct / len(heldout), 4) if heldout else None,
               "deadline_utc": deadline.isoformat()}
    write_json(output/"summary.json", summary)
    if weights:
        write_json(output/"second-turn-ranker.json", {"version": 1,
            "objective": ["lethal", "opponent-field-removal", "damage-before-main2", "main2-board-score"],
            "weights": {k: round(v, 8) for k,v in sorted(weights.items())}})
    print(json.dumps(summary), flush=True)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for name in ("fixture","scorer","boards","output"):
        p.add_argument(f"--{name}", type=Path, required=True)
    p.add_argument("--seconds",type=int,default=1800)
    p.add_argument("--jobs",type=int,default=5)
    p.add_argument("--attempts",type=int,default=140)
    p.add_argument("--search-ms",type=int,default=10000)
    p.add_argument("--seed",type=int,default=2026112771)
    p.add_argument("--max-cases",type=int)
    args=p.parse_args()
    if min(args.seconds,args.jobs,args.attempts,args.search_ms,args.max_cases or 1)<=0:
        p.error("positive bounds required")
    boards=json.loads(args.boards.read_bytes())
    if not boards:
        p.error("need a nonempty pool of opposing boards")
    out=args.output.resolve()
    shards=out/'shards'
    shards.mkdir(parents=True,exist_ok=True)
    contract={"boards_sha256":sha256(args.boards.read_bytes()).hexdigest(),
              "cards_sha256":sha256((args.fixture/'cards.cdb').read_bytes()).hexdigest(),
              "scorer_sha256":sha256(args.scorer.read_bytes()).hexdigest(),
              "windbot_sha256":sha256((args.scorer.resolve().parent/'WindBot.exe').read_bytes()).hexdigest(),
              "search_sha256":sha256((HERE/'second-turn-search.cjs').read_bytes()).hexdigest(),
              "seed":args.seed,"attempts":args.attempts,"search_ms":args.search_ms}
    cp=out/'manifest.json'
    if cp.exists() and json.loads(cp.read_bytes())!=contract:
        p.error("resume inputs differ from the saved run")
    if not cp.exists(): write_json(cp,contract)
    wall=datetime.now(timezone.utc)+timedelta(seconds=args.seconds)
    deadline=monotonic()+args.seconds
    weights=Counter()
    counts=Counter()
    heldout=[]
    main_deck,extra=read_deck()
    seen=set()

    def incorporate(result):
        counts['cases']+=1
        if result.get('error'):
            counts['errors']+=1
            return
        for key in ("attempts","settled","wins","main2","interrupted","effects"):
            counts[key]+=result[key]
        counts['verified_witnesses']+=int(result['replayVerified'])
        if not result['heldout']:
            update(weights,result['pairs'])
            counts['training_pairs']+=len(result['pairs'])
        else:
            if len(heldout) < 15000:
                heldout.extend(result['pairs'][:15000-len(heldout)])

    cached={int(path.stem):path for path in shards.glob('*.json')}
    nextid=0
    queued={}
    returned={}
    commit=0
    report(out,counts,weights,heldout,"running",wall)
    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        while monotonic()<deadline or queued:
            while (len(queued)+len(returned)<args.jobs and deadline-monotonic()>4
                    and (args.max_cases is None or nextid<args.max_cases)):
                case=make_case(boards,nextid,args.seed,main_deck,extra,seen)
                if nextid in cached:
                    rec=json.loads(cached[nextid].read_bytes())
                    if rec["case"]!=case: raise ValueError("saved case inputs changed")
                    returned[nextid]=rec
                else:
                    queued[pool.submit(run_case,case,args,deadline)]=case
                nextid+=1
            while commit in returned:
                rec=returned.pop(commit)
                if rec is not None:
                    if commit not in cached:write_json(shards/f'{commit:06d}.json',rec)
                    incorporate(rec)
                    if counts['cases']%10==0:report(out,counts,weights,heldout,"running",wall)
                commit+=1
            if not queued:
                if nextid==commit and (deadline-monotonic()<=4 or (args.max_cases is not None and nextid>=args.max_cases)):
                    break
                continue
            done,_=wait(queued,return_when=FIRST_COMPLETED)
            for future in done:
                case=queued.pop(future)
                try:
                    returned[case['id']]=future.result()
                except subprocess.TimeoutExpired:
                    returned[case['id']]=None
                except RuntimeError as error:
                    print(f"case {case['id']} failed: {error}",flush=True)
                    returned[case['id']]={"case":case,"error":str(error)}
    result="completed" if counts['training_pairs'] else "no_training_pairs"
    report(out,counts,weights,heldout,result,wall)
    return int(not counts['training_pairs'])


if __name__=="__main__":
    raise SystemExit(main())
