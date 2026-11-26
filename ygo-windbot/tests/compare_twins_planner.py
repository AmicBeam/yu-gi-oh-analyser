#!/usr/bin/env python3
"""Compare WindBot with independent engine search or run targeted live duel checks."""

import argparse
from collections import Counter
from contextlib import contextmanager
import json
import os
from pathlib import Path
import random
import re
import shutil
import socket
import subprocess
import tempfile
import time

from twins_engine_reference import EngineReference, read_standard, write_json


STARTERS = {36326160, 73810864, 25150, 54257392, 81078880, 82699999, 61976639, 37582948}
KNOWN_HANDS = [
    [82699999, 27204311, 14558128, 42141493, 23434538],
    [36326160, 25151, 27204311, 14558128, 42141493],
    [73810864, 27204311, 14558128, 42141493, 23434538],
    [61976639, 25156, 25151, 14558128, 42141493],
]
SINGLE_TWIN_HANDS = [[starter, 27204311, 14558128, 42141493, 23434538] for starter in (36326160, 73810864)]
GY = 16
MONSTER_ZONE = 4
DESIRAE = 82135803
FROST = 54257392
GRYPHON = 65330383
ABAO = 4731783
DEAL, NIGHTFALL, SUNNY = 6636319, 25154, 93672138
NEW_YEAR, PASSWORD = 25150, 61976639
RED_LINK = 9205574
HEIST = 25153
LINK_TWOS = {RED_LINK, 36609519, HEIST, 24269961, 49867899}
ASH, TROUBLE, REQUIEM, SWEET = 14558128, 37582948, 2463794, 82699999
OPPONENT_ACTIONS = [7572887, 75782277, 19523799, 43422537, 31305911]
SINGLE_SWEET_HAND = [SWEET, *SINGLE_TWIN_HANDS[0][1:]]
OPENING_UT_CASES = (
    ("red", SINGLE_TWIN_HANDS[0], 0),
    ("blue", SINGLE_TWIN_HANDS[1], 0),
    ("sweet", SINGLE_SWEET_HAND, 0),
    ("red-ash-requiem", SINGLE_TWIN_HANDS[0], REQUIEM),
    ("sweet-ash-requiem", SINGLE_SWEET_HAND, REQUIEM),
)
SEARCH_EXTENDER_HANDS = [
    [starter, search, extender, 42141493, 10045474]
    for starter in (36326160, 73810864, SWEET, NEW_YEAR)
    for search, extender in (
        (TROUBLE, FROST), (PASSWORD, FROST), (25151, FROST),
        (TROUBLE, 81078880), (25151, 60764609),
    )
]


def card_effects(events):
    chain, effects = {}, set()
    for name, *fields in events:
        if name == "Chaining":
            code, controller, location, desc, index = fields
            chain[index] = (code, location, desc) if controller == 0 else None
        elif name in ("ChainDisabled", "ChainNegated"):
            chain.pop(fields[0], None)
        elif name == "ChainSolved":
            effect = chain.pop(fields[0], None)
            if effect:
                effects.add(effect)
    return effects


def link_two_effects(effects):
    return sorted({(code, desc) for code, _, desc in effects if code in LINK_TWOS})


def effect_moves(events, source):
    chain = {}
    resolving = None
    for event in events:
        if event[0] == "Chaining":
            chain[event[5]] = event[1] if event[2] == 0 else None
        elif event[0] == "ChainSolving":
            resolving = chain.get(event[1])
        elif event[0] == "ChainSolved":
            chain.pop(event[1], None)
            resolving = None
        elif resolving == source and event[0] == "Move":
            yield event


def update_field(field, event):
    if event[0] != "Move":
        return
    _, code, previous, current, zone, before_player, before_zone, after_player = event
    if previous == MONSTER_ZONE and before_player == 0:
        field.pop(before_zone, None)
    if current == MONSTER_ZONE and after_player == 0:
        field[zone] = code


def abao_revived_gryphon(events):
    return any(event[:4] == ["Move", GRYPHON, GY, MONSTER_ZONE] for event in effect_moves(events, ABAO))


def ash_prefix(events):
    field, activated = {}, []
    for event in events:
        update_field(field, event)
        if event[0] == "Chaining":
            if event[1] == ASH and event[2] == 1:
                break
            if event[2] == 0:
                activated.append(event[1])
    return {"field": list(field.values()), "password_held": 61976639 not in activated}


def check_single_twin_board(board, traps, home, away):
    our_effects, opponent_turn_effects = card_effects(home), card_effects(away)
    opponent_links = set(link_two_effects(opponent_turn_effects))
    red_draw = any((RED_LINK, code * 16) in opponent_links for code in (RED_LINK, RED_LINK - 1))
    blue_pop = any((36609519, code * 16) in opponent_links for code in (36609519, 36609518))
    return {
        "desirae_in_extra_zone": DESIRAE in board[5:],
        "gryphon": GRYPHON in board,
        "abao": ABAO in board,
        "nightfall": NIGHTFALL in board,
        "both_twin_traps": traps & 3 == 3,
        "heist_held_for_opponent_turn": not any(
            event[0] == "Move" and event[1] == HEIST and event[3] == MONSTER_ZONE and event[7] == 0
            for event in home),
        "own_turn_red_link_draw": any(code == RED_LINK and location == MONSTER_ZONE
                                      and desc in (RED_LINK * 16, (RED_LINK - 1) * 16)
                                      for code, location, desc in our_effects)
            and any(event[0] == "Draw" and event[2] == 0 for event in home),
        "opponent_turn_desirae_negate": (DESIRAE, MONSTER_ZONE, DESIRAE * 16) in opponent_turn_effects,
        "opponent_turn_desirae_trade": (DESIRAE, GY, DESIRAE * 16 + 1) in opponent_turn_effects
            and any(event[:4] == ["Move", DESIRAE, MONSTER_ZONE, GY] for event in away),
        "three_opponent_turn_link_two_effects": red_draw and blue_pop and (HEIST, HEIST * 16) in opponent_links,
    }


def check_new_year_cycle(events, effects):
    released_blue = any(event[1] in (36609518, 36609519) and event[2:4] == [MONSTER_ZONE, GY]
                        for event in effect_moves(events, NEW_YEAR))
    paid_password = False
    abao_chaining = False
    search_chain = None
    search_resolved = False
    for event in events:
        if event[0] == "Chaining":
            abao_chaining = event[1:3] == [ABAO, 0]
            if event[1:5] == [NEW_YEAR, 0, MONSTER_ZONE, NEW_YEAR * 16 + 1]:
                search_chain = event[5]
        elif event[0] == "ChainSolving":
            abao_chaining = False
        elif event[0] == "ChainSolved" and event[1] == search_chain:
            search_resolved = True
            search_chain = None
        elif search_resolved and abao_chaining and event[:4] == ["Move", PASSWORD, 2, GY]:
            paid_password = True
    return {
        "released_blue_for_new_year": released_blue,
        "new_year_searched_password": (NEW_YEAR, MONSTER_ZONE, NEW_YEAR * 16 + 1) in effects,
        "abao_paid_password": paid_password,
    }


def check_requiem_board(board, checks, effects, events, own_events):
    deal_in_extra = any(event[:4] == ["Move", DEAL, 64, MONSTER_ZONE] and event[4] >= 5
                        for event in effect_moves(events, NIGHTFALL))
    field = {zone: code for zone, code in enumerate(board) if code}
    for event in events:
        update_field(field, event)
    end_extra = {field.get(5), field.get(6)}
    open_extra = end_extra & {GRYPHON, NIGHTFALL, SUNNY, 25153}
    frost_in_grave = sum(
        int(event[3] == GY and event[7] == 0) - int(event[2] == GY and event[5] == 0)
        for event in own_events if event[:2] == ["Move", FROST]
    ) > 0
    return {
        "closed_gryphon_lock": GRYPHON in board and GRYPHON in field.values() and not open_extra,
        "deal_replaced_nightfall_in_extra": NIGHTFALL in board[5:] and deal_in_extra,
        "abao": ABAO in board,
        "abao_revived_gryphon": abao_revived_gryphon(events),
        "frost_in_grave": frost_in_grave,
        "both_twin_traps": checks["both_twin_traps"],
        "own_turn_red_link_draw": checks["own_turn_red_link_draw"],
        "three_opponent_turn_link_two_effects": checks["three_opponent_turn_link_two_effects"],
        "opponent_turn_red_and_blue": any(code == RED_LINK and desc == (RED_LINK - 1) * 16
                                          for code, _, desc in effects)
            and any(code == 36609519 and desc == 36609518 * 16 for code, _, desc in effects),
    }


def requiem_target(duel):
    checks = duel["bot"].get("opponent_turn", {}).get("requiem_checks", {})
    reference = duel["reference"].get("opponent_turn", {}).get("requiem_checks", {})
    required = ("both_twin_traps", "own_turn_red_link_draw")
    sweet = duel["hand"][0] == SWEET
    if sweet:
        required += ("abao_revived_gryphon", "opponent_turn_red_and_blue")
    else:
        required += ("closed_gryphon_lock", "three_opponent_turn_link_two_effects")
        if duel["hand"][0] == SINGLE_TWIN_HANDS[0][0]:
            required += ("abao",)
    missing = [check for check in required if not checks.get(check)]
    higher_alternative = bool(sweet and all(reference.get(check) for check in required)
                              and duel["bot"].get("score", -1) > duel["reference"].get("score", -1))
    return missing, higher_alternative


def opening_ut_target(duel, name, ash_target):
    if ash_target:
        missing, higher_alternative = requiem_target(duel)
        if name == "red-ash-requiem" and not duel["bot"].get("opponent_turn", {}).get(
            "requiem_checks", {}
        ).get("frost_in_grave"):
            missing.append("frost_in_grave")
        return {"name": name, "passed": not missing or higher_alternative,
                "missing": missing, "higher_alternative": higher_alternative}

    bot = duel["bot"]
    opponent_turn = bot.get("opponent_turn", {})
    checks = dict(opponent_turn.get("checks", {"missing_duel": False}))
    if name == "sweet":
        checks.pop("gryphon", None)
        checks.pop("desirae_in_extra_zone", None)
        checks["desirae_in_main_zone"] = DESIRAE in bot.get("board", [])[:5]
        checks["abao_revived_gryphon"] = opponent_turn.get("abao_revived_gryphon", False)
    missing = [check for check, passed in checks.items() if not passed]
    return {"name": name, "passed": not missing, "missing": missing}


def available_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def await_port(port, child):
    for _ in range(150):
        if child.poll() is not None:
            raise RuntimeError(f"process exited before opening {port}")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.1):
                return
        except OSError:
            time.sleep(0.05)
    raise RuntimeError(f"port {port} did not open")


@contextmanager
def background(args, cwd, log, env=None):
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen(args, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT)
        try:
            yield process
        finally:
            process.terminate()
            try:
                process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def fixed_deck(original, hand, seed, *, next_draw=None):
    main = list(original)
    for card in hand:
        main.remove(card)
    top = []
    if next_draw is not None:
        main.remove(next_draw)
        top.append(next_draw)
    random.Random(seed).shuffle(main)
    return list(hand) + top + main


def sample_hands(main, count, seed):
    randomizer = random.Random(seed)
    seen = {tuple(sorted(hand)) for hand in KNOWN_HANDS}
    for hand in KNOWN_HANDS:
        if not (Counter(hand) - Counter(main)):
            yield hand
    while count > 0:
        hand = randomizer.sample(main, 5)
        key = tuple(sorted(hand))
        if key in seen or not STARTERS.intersection(hand):
            continue
        seen.add(key)
        count -= 1
        yield hand


def prepare_fixture(work, fixture, windbot, bots, port, bot_port):
    for name in ("Dialogs", "cards.cdb", "data", "dist", "node_modules", "resource", "ygopro"):
        (work / name).symlink_to((fixture / name).resolve())
    (work / "windbot").symlink_to(".")
    shutil.copy2(windbot, work / "WindBot.exe")
    if Path(str(windbot) + ".config").exists():
        shutil.copy2(Path(str(windbot) + ".config"), work / "WindBot.exe.config")
    shutil.copy2(bots, work / "bots.json")
    (work / "Decks").mkdir()
    (work / "config.yaml").write_text(
        f"host: '127.0.0.1'\nport: {port}\napiPort: 0\nwsPort: 0\n"
        "dbHost: ''\nredisUrl: ''\nenableSsl: 0\nenableWindbot: 1\nenableCloudReplay: 0\n"
        "enableRecover: 0\nenableRandomDuel: 0\nenableDialogues: 0\nenableTips: 0\nenableWords: 0\n"
        "enableBadwords: 0\nygoproPath: './ygopro'\nygoproResourceRefreshMinutes: 0\n"
        f"windbotSpawn: 0\nwindbotEndpoint: 'http://127.0.0.1:{bot_port}'\nwindbotMyIp: '127.0.0.1'\n",
        encoding="utf-8",
    )


def play(work, output, label, seat, port, bot_port, width=None, states=None, *,
         second=False, opponent_monster=0, ash_target=0, export_table=False, check_opponent_turn=False,
         interference=None, reference_ms=10000):
    log = output / (label + ".log")
    variables = dict(os.environ, NODE_PATH=str(work / "node_modules"), TWINS_PLAN_TRACE="1")
    for flag in ("TWINS_PLAN_WIDTH", "TWINS_PLAN_STATES", "TWINS_PLAN_REFERENCE_MS",
                 "TWINS_PLAN_FREEZE_OPENING", "TWINS_PLAN_EXPORT"):
        variables.pop(flag, None)
    if (work / "Decks" / "AI_Twins.plan.jsonl").is_file():
        variables["TWINS_PLAN_OPENING_TABLE"] = "1"
    else:
        variables.pop("TWINS_PLAN_OPENING_TABLE", None)
    if second:
        variables.update(TWINS_GO_SECOND="1", TWINS_OPPONENT_MONSTER=str(opponent_monster))
    if ash_target:
        variables["TWINS_ASH_TARGET"] = str(ash_target)
    if interference:
        variables["TWINS_INTERFERENCE"] = interference
    if check_opponent_turn:
        variables["TWINS_CHECK_OPPONENT_TURN"] = "1"
    if width is not None:
        variables.update(TWINS_PLAN_WIDTH=str(width), TWINS_PLAN_STATES=str(states),
                         TWINS_PLAN_REFERENCE_MS=str(reference_ms))
        if ash_target == REQUIEM and (work / "Decks" / "AI_Twins.plan.jsonl").is_file():
            variables["TWINS_PLAN_FREEZE_OPENING"] = "1"
        if export_table:
            variables["TWINS_PLAN_EXPORT"] = "1"
    with background(["mono", "WindBot.exe", "ServerMode=true", f"ServerPort={bot_port}"], work, log, variables) as child:
        await_port(bot_port, child)
        for attempt in range(16 if second else 6):
            result = subprocess.run(
                ["node", str(seat), str(port), str(work / "opponent.json")],
                cwd=work, env=dict(variables, TWINS_RPS=str(attempt)), capture_output=True,
                text=True, timeout=50,
            )
            if result.returncode == 0:
                break
            if "RETRY_SEAT" not in result.stderr:
                raise RuntimeError(result.stderr.strip())
        if result.returncode:
            raise RuntimeError(result.stderr.strip())
    (output / (label + ".json")).write_text(result.stdout, encoding="utf-8")
    transcript = log.read_text(encoding="utf-8").rsplit("Duel started:", 1)[-1]
    observed = re.findall(r"TwinsPlan observed (\d+) ([\d,]+) (\d+) (\d+)", transcript)
    projected = re.findall(r"TwinsPlan plan (\d+) (\d+) (\d+)", transcript)
    if not observed or not projected:
        raise RuntimeError(f"missing planner observations in {log}")
    score, board, traps, draws = observed[-1]
    if ash_target and ("TwinsPlan recovery opponent-chain" not in transcript or int(score) <= 32):
        raise RuntimeError(f"the fixture did not recover to a stronger board in {log}")
    prediction, visited, cutoffs = projected[0]
    trace = json.loads(result.stdout)
    turns = [i for i, entry in enumerate(trace) if entry[0] == "NewTurn"]
    turn_index = 1 if second else 0
    if ash_target:
        own_turn = trace[turns[0]:turns[1]]
        responses = [e for e in own_turn if e[0] in ("Chaining", "ChainDisabled", "ChainSolved")]
        ash_negated_target = any(
            target[0] == response[0] == "Chaining" and target[1] == ash_target and response[1] == ASH
            and any(e == ["ChainDisabled", target[5]] for e in responses[index + 2:index + 6])
            for index, (target, response) in enumerate(zip(responses, responses[1:]))
        )
        if not ash_negated_target:
            raise RuntimeError(f"the fixture did not negate {ash_target} with Ash in {log}")
    opponent_turn = {}
    if check_opponent_turn:
        if len(turns) < 3:
            raise RuntimeError(f"the opponent turn did not complete in {log}")
        events = trace[turns[1]:turns[2]]
        field = list(map(int, board.split(",")))
        effects = card_effects(events)
        checks = check_single_twin_board(field, int(traps), trace[turns[0]:turns[1]], events)
        opponent_turn = {
            "activations": [event[1:5] for event in events if event[0] == "Chaining"],
            "draws": sum(event[0] == "Draw" and event[2] == 0 for event in events),
            "link_two_effects": link_two_effects(effects),
            "checks": checks,
            "abao_revived_gryphon": abao_revived_gryphon(events),
            "new_year_cycle": check_new_year_cycle(events, effects),
        }
        if ash_target == REQUIEM:
            opponent_turn["requiem_checks"] = check_requiem_board(
                field, checks, effects, events, trace[turns[0]:turns[1]]
            )
            opponent_turn["ash_prefix"] = ash_prefix(trace[turns[0]:turns[1]])
    window = re.search(r"TwinsPlan window (\d+) (\w+) (\d+) (\d+)", transcript)
    if not window or window.group(2) != ("Main2" if second else "Main1"):
        raise RuntimeError(f"unexpected planning phase in {log}")
    if second and opponent_monster:
        if not any(event[:4] == ["Move", opponent_monster, 4, 16] for event in trace[:turns[2]]):
            raise RuntimeError(f"the test monster was not cleared in {log}")
    reference = re.search(r"TwinsPlan reference (\d+) (\d+) (\w+)", transcript)
    if width is not None:
        if not reference:
            raise RuntimeError(f"missing reference candidate scores in {log}")
        base_score, wide_score, selected = reference.groups()
        if int(prediction) != max(int(base_score), int(wide_score)):
            raise RuntimeError(f"reference dropped a candidate in {log}")
    search_times = list(map(int, re.findall(r"TwinsPlan search-ms (\d+)", transcript)))
    evaluations = re.findall(r"TwinsPlan policy-evaluations (\d+) (\d+)", transcript)
    table_entries = []
    if export_table and not any(f"TwinsPlan {event}" in transcript
            for event in ("resync ", "selection-mismatch ", "placement-mismatch ")):
        table_entries = [json.loads(line.split(" ", 2)[2]) for line in transcript.splitlines()
                         if line.startswith("TwinsPlan table-entry ")]
    return {
        "score": int(score) / 10, "projected": int(prediction) / 10, "states": int(visited),
        "pruned": int(cutoffs), "search_ms": sum(search_times),
        "reference_search_ms": sum(map(int, re.findall(r"TwinsPlan reference-ms (\d+)", transcript))),
        "policy_nodes": sum(int(nodes) for nodes, _ in evaluations),
        "policy_score_evaluations": sum(int(calls) for _, calls in evaluations),
        "max_search_ms": max(search_times, default=0),
        "max_main_gap_ms": next((event[1] for event in trace if event[0] == "MaxMainGapMs"), None),
        "first_action_ms": next((event[1] for event in trace if event[0] == "FirstActionMs"), None),
        "moves": transcript.count("TwinsPlan move "),
        "table_hits": transcript.count("TwinsPlan table-hit "),
        "tactical_hits": transcript.count("TwinsPlan tactic-plan "),
        **({"table_entries": table_entries} if export_table else {}),
        "recoveries": transcript.count("TwinsPlan recovery "),
        "resync": transcript.count("TwinsPlan resync "),
        "choice_mismatches": transcript.count("TwinsPlan selection-mismatch "),
        "placement_mismatches": transcript.count("TwinsPlan placement-mismatch "),
        "draws": max(0, sum(entry[0] == "Draw" for entry in trace[turns[turn_index]:turns[turn_index + 1]]) - int(second)),
        "phase": window.group(2), "normal_used": bool(int(window.group(3))), "used_effect_mask": int(window.group(4)),
        **({"incumbent_projected": int(base_score) / 10, "wide_only_projected": int(wide_score) / 10,
            "selected": selected} if reference else {}),
        "planner_draws": int(draws), "board": list(map(int, board.split(","))), "traps": int(traps),
        **({"opponent_turn": opponent_turn} if check_opponent_turn else {}),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", required=True, type=Path, help="local SRVPro2 fixture with dist, cards and node_modules")
    parser.add_argument("--windbot", required=True, type=Path, help="compiled WindBot.exe with this overlay")
    parser.add_argument("--output", required=True, type=Path, help="directory for per-duel traces and report.json")
    parser.add_argument("--table", type=Path, help="opening table for the production bot")
    parser.add_argument("--table-out", type=Path, help="export candidates verified by the offline reference duel")
    parser.add_argument("--reference-in", type=Path, help="compare against a frozen standard answer set")
    parser.add_argument("--hand", help="use exactly this hand, as five comma-separated card codes")
    parser.add_argument("--samples", type=int, default=20)
    parser.add_argument("--seed", type=int, default=20261123)
    parser.add_argument("--from-case", type=int, default=0, help="skip earlier cases with the same seed")
    parser.add_argument("--to-case", type=int, help="last case index to run, inclusive")
    parser.add_argument("--second", action="store_true", help="test a Main Phase 2 handoff")
    parser.add_argument("--opponent-monster", type=int, default=0, help="normal summon this monster in the going-second scenario")
    parser.add_argument("--ash-trouble", action="store_true", help="replay the Ash-on-Trouble opening fixture")
    parser.add_argument("--ash-requiem", action="store_true", help="Ash Requiem for each single starter and play the opponent turn")
    parser.add_argument("--check-opponent-turn", action="store_true", help="play a monster and spells into the opening board")
    parser.add_argument("--single-twin-board", action="store_true",
                        help="require the Desirae/Gryphon/Abao board and its opponent-turn effects for both single starters")
    parser.add_argument("--opening-ut", action="store_true",
                        help="check the five accepted Red, Blue, and Sweet openings and Requiem Ash recoveries")
    parser.add_argument("--search-matrix", action="store_true",
                        help="compare fixed single starters with search and extender combinations")
    parser.add_argument("--max-wait-ms", type=int, help="fail if the production bot exceeds this time between main-phase messages")
    parser.add_argument("--max-gap", type=float, default=None, help="fail if the reference wins by more than this")
    parser.add_argument("--wide-search", action="store_true", help="diagnose with WindBot’s wider shared planner")
    parser.add_argument("--reference-width", type=int, default=1200)
    parser.add_argument("--reference-states", type=int, default=360000)
    parser.add_argument("--reference-ms", type=int, default=10000, help="reference search budget in milliseconds per duel")
    args = parser.parse_args()
    if args.reference_ms <= 0:
        parser.error("--reference-ms must be positive")
    if args.search_matrix and (args.hand or args.opening_ut or args.single_twin_board
                               or args.ash_trouble or args.ash_requiem):
        parser.error("--search-matrix supplies its own set of starting hands")
    if args.opening_ut and (args.hand or args.second or args.ash_trouble or args.ash_requiem
                            or args.single_twin_board or args.from_case or args.to_case is not None):
        parser.error("--opening-ut runs all five opening scenarios")
    if args.ash_requiem and (args.ash_trouble or args.second):
        parser.error("--ash-requiem requires a going-first fixture with one interruption")
    if args.single_twin_board and (args.hand or args.ash_requiem):
        parser.error("--single-twin-board includes both single-starter hands")
    args.check_opponent_turn |= args.single_twin_board or args.opening_ut
    if args.check_opponent_turn and (args.second or args.ash_trouble):
        parser.error("--check-opponent-turn requires a supported going-first fixture")
    args.check_opponent_turn |= args.ash_requiem
    independent = not (args.wide_search or args.second or args.ash_trouble or args.ash_requiem
                       or args.opening_ut or args.single_twin_board or args.check_opponent_turn or args.table_out)
    if args.reference_in and (not independent or args.hand or args.search_matrix):
        parser.error("--reference-in uses the hands and configuration frozen in the independent answer set")
    frozen = read_standard(args.reference_in, "opening") if args.reference_in else None
    ash_target = REQUIEM if args.ash_requiem else TROUBLE if args.ash_trouble else 0
    overlay = Path(__file__).resolve().parent.parent
    seat = overlay / "tests" / "twins-seat.cjs"
    main_deck, extra = (overlay / "Decks" / "AI_Twins.ydk").read_text().split("#extra\n")
    main_deck = [int(card) for card in main_deck.split("#main\n")[1].splitlines()]
    if args.ash_trouble and args.second:
        parser.error("--ash-trouble tests the bot going first")
    ash_hand = [37582948, 25150, 59438930, 10045474, 36326160]
    if args.hand and args.ash_trouble:
        parser.error("--hand and --ash-trouble cannot be used together")
    if frozen:
        hands = [item["input"]["main"][:5] for item in frozen]
    elif args.opening_ut:
        hands = [hand for _, hand, _ in OPENING_UT_CASES]
    elif args.search_matrix:
        hands = SEARCH_EXTENDER_HANDS
    elif args.single_twin_board:
        hands = SINGLE_TWIN_HANDS
    elif args.hand:
        try:
            hand = [int(code) for code in args.hand.split(",")]
        except ValueError:
            parser.error("--hand requires comma-separated card codes")
        if len(hand) != 5 or Counter(hand) - Counter(main_deck):
            parser.error("--hand must contain five cards in the main deck")
        hands = [hand]
    elif args.ash_requiem:
        hands = [*SINGLE_TWIN_HANDS, SINGLE_SWEET_HAND]
    else:
        hands = [ash_hand] if args.ash_trouble else sample_hands(main_deck, args.samples, args.seed)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    standard_path = output / "references.json"
    if independent and not frozen and standard_path.exists():
        parser.error("--output already contains a frozen answer set; use a new output directory")
    if args.reference_in and args.reference_in.resolve() in (output / "report.json", output / "summary.json"):
        parser.error("--output would overwrite the supplied reference")
    report, standard = [], []
    with tempfile.TemporaryDirectory(prefix="twins-compare-") as directory:
        work = Path(directory)
        port, bot_port = available_port(), available_port()
        while bot_port == port:
            bot_port = available_port()
        prepare_fixture(work, args.fixture, args.windbot, overlay / "bots.json", port, bot_port)
        if args.table:
            shutil.copy2(args.table, work / "Decks" / "AI_Twins.plan.jsonl")
        extra_cards = [int(card) for card in extra.split("!side")[0].splitlines()]
        engine = (EngineReference(work, args.fixture, args.windbot, output, args.reference_ms)
                  if independent and not frozen else None)
        opponent = fixed_deck(main_deck, [args.opponent_monster], args.seed) if args.opponent_monster else main_deck
        with background(["node", "dist/index.js"], work, output / "server.log") as server:
            await_port(port, server)
            for index, hand in enumerate(hands):
                if index < args.from_case:
                    continue
                if args.to_case is not None and index > args.to_case:
                    break
                name, _, case_ash = OPENING_UT_CASES[index] if args.opening_ut else (None, hand, ash_target)
                case_opponent = fixed_deck(main_deck, [ASH], args.seed) if case_ash else opponent
                if args.check_opponent_turn:
                    opponent_hand = [ASH, *OPPONENT_ACTIONS] if case_ash else OPPONENT_ACTIONS
                    case_opponent = [*opponent_hand, *case_opponent[len(opponent_hand):]]
                # The Red and Blue fixtures should give Abao the same draw to work with.
                next_draw = ASH if args.opening_ut and name in ("red", "blue") else None
                engine_input = frozen[index]["input"] if frozen else {
                    "main": fixed_deck(main_deck, hand, args.seed + index, next_draw=next_draw),
                    "extra": extra_cards, "draw_seed": args.seed + index,
                    "opponent": {"main": case_opponent, "extra": []},
                }
                (work / "opponent.json").write_text(json.dumps(engine_input["opponent"]), encoding="utf-8")
                (work / "Decks" / "AI_Twins.ydk").write_text(
                    "#main\n" + "\n".join(map(str, engine_input["main"]))
                    + "\n#extra\n" + "\n".join(map(str, engine_input["extra"])) + "\n", encoding="utf-8",
                )
                entry = {"hand": hand, "case": index}
                for label, limits in (("bot", ()), ("reference", (args.reference_width, args.reference_states))):
                    try:
                        if label == "reference" and independent:
                            entry[label] = frozen[index]["reference"] if frozen else engine.best(engine_input, str(index))
                            continue
                        entry[label] = play(work, output, f"{index}-{label}", seat, port, bot_port, *limits,
                                            second=args.second, opponent_monster=args.opponent_monster,
                                            ash_target=case_ash, export_table=bool(args.table_out and label == "reference"),
                                            check_opponent_turn=args.check_opponent_turn, reference_ms=args.reference_ms)
                    except (RuntimeError, subprocess.TimeoutExpired) as reason:
                        entry[label] = {"error": str(reason)}
                if independent and "score" in entry["reference"]:
                    standard.append({"hand": hand, "input": engine_input, "reference": entry["reference"]})
                if all("score" in entry[side] for side in ("bot", "reference")):
                    if not independent:
                        # A real-time cutoff can stop the two shared planner runs at different nodes.
                        entry["incumbent_delta"] = round(
                            entry["reference"]["incumbent_projected"] - entry["bot"]["projected"], 1)
                    entry["raw_gap"] = round(entry["reference"]["score"] - entry["bot"]["score"], 1)
                    entry["gap"] = max(0.0, entry["raw_gap"])
                if args.opening_ut:
                    entry["opening_ut"] = opening_ut_target(entry, name, case_ash)
                report.append(entry)
                (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
                print(json.dumps(entry), flush=True)
    if args.table_out:
        best_entries = {}
        for duel in report:
            for entry in duel["reference"].get("table_entries", []):
                old = best_entries.get(entry["Key"])
                if duel["reference"]["score"] * 10 < entry["Score"]:
                    continue
                if old is None or entry["Score"] > old["Score"]:
                    best_entries[entry["Key"]] = entry
        args.table_out.write_text("".join(json.dumps(row, separators=(",", ":")) + "\n"
                                          for row in best_entries.values()), encoding="utf-8")
    errors = sum("error" in duel[side] for duel in report for side in ("bot", "reference"))
    mismatches = {
        key: sum(duel[side].get(key, 0) for duel in report for side in ("bot", "reference"))
        for key in ("resync", "choice_mismatches", "placement_mismatches")
    }
    gaps = [duel["gap"] for duel in report if "gap" in duel]
    waits = [r["bot"]["max_main_gap_ms"] for r in report if r["bot"].get("max_main_gap_ms") is not None]
    weak_references = sum(duel.get("raw_gap", 0) < 0 for duel in report) if independent else 0
    standard_written = bool(independent and not frozen and len(standard) == len(report) and report
                            and not weak_references and not errors)
    if standard_written:
        write_json(standard_path, {"version": 2, "mode": "opening", "reference_ms": args.reference_ms,
                                   "cases": standard})
    summary = {
        "reference_method": "engine_search" if independent else "legacy_wide_search",
        "weak_references": weak_references,
        "standard_written": standard_written,
        "max_main_gap_ms": max(waits, default=None),
        "max_search_ms": max((r["bot"].get("max_search_ms", 0) for r in report), default=0),
        "seed": args.seed, "cases": len(report), "errors": errors, "equal": sum(duel.get("raw_gap") == 0 for duel in report),
        **mismatches,
        "reference_higher": sum(gap > 0 for gap in gaps), "max_gap": max(gaps, default=0),
        "incumbent_variations": sum(duel.get("incumbent_delta", 0) != 0 for duel in report),
        "wide_only_misses": sum(duel["reference"].get("selected") == "incumbent"
            and duel["reference"]["wide_only_projected"] < duel["reference"]["incumbent_projected"] for duel in report),
    }
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary), flush=True)
    slow = args.max_wait_ms is not None and (len(waits) != len(report) or any(ms > args.max_wait_ms for ms in waits))
    single_twin_failed = args.single_twin_board and (len(report) != len(SINGLE_TWIN_HANDS)
        or any(not all(duel["bot"].get("opponent_turn", {}).get("checks", {"missing": False}).values()) for duel in report))
    requiem_failed = False
    if args.ash_requiem:
        for duel in report:
            missing, higher_alternative = requiem_target(duel)
            duel["requiem_target_met"] = not missing
            duel["requiem_higher_alternative"] = higher_alternative
            requiem_failed |= not (duel["requiem_target_met"] or higher_alternative)
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    opening_ut_failed = args.opening_ut and (len(report) != len(OPENING_UT_CASES)
        or any(not duel["opening_ut"]["passed"] for duel in report))
    return int(errors != 0 or slow or single_twin_failed or requiem_failed or opening_ut_failed or weak_references
               or args.max_gap is not None and (summary["max_gap"] > args.max_gap or any(mismatches.values())))


if __name__ == "__main__":
    raise SystemExit(main())
