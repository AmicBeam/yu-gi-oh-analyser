"""Compare three variants in fixed-seed, mutually held-out SRVPro2 duels."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
from statistics import mean

from mixed_benchmark import run_group
from train import hand_key
from compare_twins_planner import available_port
from twins_engine_reference import write_json

parser = argparse.ArgumentParser(description=__doc__)
for opt in ('fixture', 'windbot', 'previous', 'run'):
    parser.add_argument(f'--{opt}', type=Path, required=True)
parser.add_argument('--jobs', type=int, default=4)
parser.add_argument('--case', action='append', default=[])
parser.add_argument('--output', type=Path)
args = parser.parse_args()
names = json.loads((args.run/'holdout-inputs.json').read_text())
cases = [case for case in names if not args.case or case['id'] in args.case]
if not cases or len(cases) != (len(args.case) if args.case else len(names)):
    parser.error('Unknown or repeated case')
trained = json.loads((args.run/'training-inputs.json').read_text())
if {hand_key(c['hand']) for c in cases} & {hand_key(c['hand']) for c in trained}:
    raise RuntimeError('Holdout contains a training opening')
output = args.output or args.run/'audit'
output.mkdir(parents=True)
modes = {'rule': None, 'previous': args.previous.resolve(strict=True),
         'hourly': (args.run/'twins-branch-ranker.tsv').resolve(strict=True)}
jobs = min(args.jobs, len(cases))
ports = set()
while len(ports) < jobs * 2:
    ports.add(available_port())
port_pairs = iter(sorted(ports))
with ThreadPoolExecutor(max_workers=jobs) as pool:
    tasks = [pool.submit(run_group, args.fixture, args.windbot, output, index, pair,
                         cases[index::jobs], modes, clean_controls=False)
             for index, pair in enumerate(zip(port_pairs,port_pairs))]
    for task in tasks:
        task.result()
rows = []
error_count = dict.fromkeys(modes, 0)
landed_cards = {mode: set() for mode in modes}
for case in cases:
    row = json.loads((output/f"{case['id']}-result.json").read_text())
    games = {mode: next(iter(result.values())) for mode,result in row['strategies'].items()}
    row['adjusted'] = {}
    for mode, result in games.items():
        if 'score' not in result:
            error_count[mode] += 1
            continue
        landed_cards[mode].update(e['card'] for e in result['activations'] if e['landed'])
        row['adjusted'][mode] = round(result['score'] - result['opponent_draws'], 3)
    rows.append(row)
bands = {}
for index in range(6):
    group = [row for row in rows if len(row['opening_traps']) == index]
    paired = [row for row in group if len(row['adjusted']) == len(modes)]
    bands[str(index)] = {'cases': len(group), 'comparable': len(paired),
                         'mean_adjusted': {mode: round(mean(row['adjusted'][mode] for row in paired), 3)
                                           for mode in modes} if paired else {}}
deltas = {key: [(row['id'], round(row['adjusted']['hourly'] - row['adjusted'][key], 3))
                for row in rows if {'hourly',key} <= row['adjusted'].keys()] for key in ('rule','previous')}
report = {'cases': len(rows), 'errors': error_count, 'bands': bands,
          'landed_cards': {key: sorted(value) for key, value in landed_cards.items()},
          'deltas': {key: {'mean': round(mean(d for _, d in values), 3) if values else None,
                           'higher': sum(d > 0 for _, d in values), 'equal': sum(d == 0 for _, d in values),
                           'lower': sum(d < 0 for _, d in values),
                           'worst': sorted(values, key=lambda value: value[1])[:4]} for key, values in deltas.items()}}
write_json(output/'report.json', rows)
write_json(output/'summary.json', report)
print(json.dumps(report, ensure_ascii=False), flush=True)
