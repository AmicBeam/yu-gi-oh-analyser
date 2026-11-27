"""Compare Hand-Rip Magistus YDKs with paired, stratified opening samples."""

import argparse
import json
import os
import subprocess
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from itertools import repeat
from math import comb
from pathlib import Path
from random import Random
from statistics import fmean

HERE = Path(__file__).resolve().parent
HAND_SIZE = 5
FILLER = 89631139
HAND_TRAPS = {
  23434538: 1,
  14558127: 2,
  10045474: 3,
  97268402: 3,
  27204311: 3,
  40366667: 2,
  97045737: 2,
  65302903: 2,
  59438930: 3,
  73642296: 2,
  42141493: 3,
  84192580: 3,
}


def ydk(path):
  sections = {"#main": [], "#extra": [], "!side": []}
  section = None
  for raw in path.read_text(encoding="utf-8-sig").splitlines():
    line = raw.strip()
    if line in sections:
      section = sections[line]
    elif line.isdecimal() and section is not None:
      section.append(int(line))
  return sections["#main"], sections["#extra"]


def paired_slots(base, variant):
  if len(base) != len(variant):
    raise ValueError("Paired sampling requires the same main-deck size")
  remaining = Counter(variant)
  slots = []
  for card in base:
    if remaining[card] > 0:
      remaining[card] -= 1
      slots.append(card)
    else:
      slots.append(None)
  additions = iter(remaining.elements())
  return [next(additions) if card is None else card for card in slots]


def opponent_deck(traps, size, count, rng):
  remaining = traps.copy()
  chosen = rng.sample(range(len(remaining)), count)
  hand = [remaining[index] for index in chosen]
  for index in sorted(chosen, reverse=True):
    del remaining[index]
  blanks = size - len(traps)
  hand.extend(repeat(FILLER, HAND_SIZE - count))
  remaining.extend(repeat(FILLER, blanks - HAND_SIZE + count))
  rng.shuffle(hand)
  rng.shuffle(remaining)
  return hand + remaining


def run_partition(args, inputs):
  env = dict(os.environ, NODE_PATH=str(args.fixture / "node_modules"))
  command = ["node", str(HERE / "search-hand.cjs"), str(args.fixture), *(str(p) for p in args.data)]
  result = subprocess.run(
    command,
    input="".join(json.dumps(task) + "\n" for task in inputs),
    text=True,
    capture_output=True,
    env=env,
    check=False,
  )
  if result.returncode:
    raise RuntimeError(result.stderr or f"Search failed with status {result.returncode}")
  rows = [json.loads(line) for line in result.stdout.splitlines()]
  if [row["id"] for row in rows] != [task["id"] for task in inputs]:
    raise RuntimeError("The search worker returned an incomplete or out-of-order batch")
  return rows


def summarize(rows, probs):
  stats = {}
  for row in rows:
    label, count, _ = row["id"]
    stats.setdefault(label, {}).setdefault(count, []).append(row)

  summaries = {}
  for label, strata in stats.items():
    conditional = {}
    for count, samples in strata.items():
      boards = [row["best"] for row in samples if row["best"] is not None]
      conditional[count] = {
        "n": len(samples),
        "settled": len(boards),
        "mean": fmean(b["score"] for b in boards) / 10 if boards else None,
        "rip_mean": fmean(b["lost"] + b["extra"] for b in boards) if boards else None,
        "draw_mean": fmean(b["draws"] for b in boards) if boards else None,
        "best": max((b["score"] / 10 for b in boards), default=None),
        "positive_rate": sum(b["score"] > 0 for b in boards) / len(boards) if boards else None,
        "net_rip_rate": sum(b["net"] > 0 for b in boards) / len(boards) if boards else None,
      }
    complete = all(
      count in conditional and conditional[count]["settled"] == conditional[count]["n"]
      for count, prob in probs.items()
      if prob
    )
    summaries[label] = {
      "expected_score": sum(
        prob * conditional[count]["mean"] for count, prob in probs.items() if prob
      )
      if complete
      else None,
      "strata": conditional,
    }
  return summaries


def main():
  parser = argparse.ArgumentParser(description=__doc__)
  parser.add_argument(
    "deck", type=Path, nargs="+", help="the original YDK followed by candidate YDKs"
  )
  parser.add_argument("--fixture", type=Path, required=True)
  parser.add_argument(
    "--data", type=Path, nargs="+", required=True, help="card/script data dirs, base first"
  )
  parser.add_argument("--output", type=Path, required=True)
  parser.add_argument(
    "--samples", type=int, default=12, help="number of common hands per trap count"
  )
  parser.add_argument("--budget-ms", type=int, default=1000, help="time per duel")
  parser.add_argument(
    "--attempts", type=int, help="optional rollout cap in addition to the time limit"
  )
  parser.add_argument("--workers", type=int, default=4)
  parser.add_argument("--seed", type=int, default=20261126)
  parser.add_argument(
    "--allow-missing-script",
    type=int,
    nargs="*",
    default=[],
    help="model these cards as having no script and record the limitation",
  )
  args = parser.parse_args()
  if min(args.samples, args.budget_ms, args.workers, args.attempts or 1) < 1:
    parser.error("samples, budget, workers and attempt cap must be positive")

  decks = [(entry.stem, *ydk(entry)) for entry in args.deck]
  if len({name for name, _, _ in decks}) != len(decks):
    parser.error("YDK names must be unique")
  base = decks[0][1]
  if len(base) < HAND_SIZE:
    parser.error("each YDK needs at least five main-deck cards")
  aligned = [(name, paired_slots(base, main), extra) for name, main, extra in decks]
  traps = list(Counter(HAND_TRAPS).elements())
  size = 40
  probabilities = {
    count: comb(len(traps), count)
    * comb(size - len(traps), HAND_SIZE - count)
    / comb(size, HAND_SIZE)
    for count in range(HAND_SIZE + 1)
  }

  rng = Random(args.seed)
  inputs = []
  for count, probability in probabilities.items():
    if not probability:
      continue
    for trial in range(args.samples):
      positions = list(range(len(base)))
      rng.shuffle(positions)
      opponent = opponent_deck(traps, size, count, rng)
      draw_seed = rng.randrange(2**32)
      for name, deck, extra in aligned:
        inputs.append(
          {
            "id": [name, count, trial],
            "budget": args.budget_ms,
            **({"attempts": args.attempts} if args.attempts is not None else {}),
            "allowMissingScripts": args.allow_missing_script,
            "input": {
              "main": [deck[index] for index in positions],
              "extra": extra,
              "opponent": {"main": opponent, "extra": []},
              "draw_seed": draw_seed,
              "trapCount": count,
            },
          }
        )

  workers = min(args.workers, len(inputs))
  partitions = [inputs[index::workers] for index in range(workers)]
  with ThreadPoolExecutor(max_workers=workers) as executor:
    batches = executor.map(lambda tasks: run_partition(args, tasks), partitions)
    rows = [entry for batch in batches for entry in batch]
  task_data = {tuple(task["id"]): task["input"] for task in inputs}
  for row in rows:
    input = task_data[tuple(row["id"])]
    row["opening"] = input["main"][:HAND_SIZE]
    row["opponent_opening"] = input["opponent"]["main"][:HAND_SIZE]
    row["draw_seed"] = input["draw_seed"]
  rows.sort(key=lambda row: row["id"])
  summaries = summarize(rows, probabilities)
  args.output.parent.mkdir(parents=True, exist_ok=True)
  args.output.write_text(
    json.dumps(
      {
        "seed": args.seed,
        "budget_ms": args.budget_ms,
        "attempts": args.attempts,
        "deck_size": size,
        "hand_traps": HAND_TRAPS,
        "count_probabilities": probabilities,
        "missing_script_allowlist": args.allow_missing_script,
        "summary": summaries,
        "cases": rows,
      },
      ensure_ascii=False,
      indent=2,
    )
    + "\n",
    encoding="utf-8",
  )
  print(json.dumps(summaries, ensure_ascii=False, indent=2))


if __name__ == "__main__":
  main()
