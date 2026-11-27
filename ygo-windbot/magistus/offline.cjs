'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { parseArgs } = require('node:util');
const { loadEngine, rollout, compare } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { magistusTie } = require('../turn-evidence.cjs');
const { card, trap, ownPolicy, opponentChoice } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');
const { UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { DRAW_COST_TENTHS, PROMOTION_RULE, adjusted, judge } = require('./promotion.cjs');

const MODEL = 'magistus-action-ranker-v1';
const SCORE_REVISION = 'draw-transform-moon-droll-little-knight-v1';
const SEARCH_REVISION = 'verified-pass-fallback-build-zero-v2';
// User's 40-card pressure deck. The remaining cards provide no interaction.
const incoming = new Map([[23434538, 1], [14558127, 2], [10045474, 3], [97268402, 3],
    [27204311, 3], [40366667, 2], [97045737, 2], [65302903, 2], [59438930, 3],
    [73642296, 2], [42141493, 3], [84192580, 3]]);
const trapDeck = [...incoming].flatMap(([id, count]) => Array(count).fill(id));
const blank = 89631139;

function read(file) {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function write(file, value) {
    const temporary = file + '.tmp';
    fs.writeFileSync(temporary, JSON.stringify(value) + '\n');
    fs.renameSync(temporary, file);
}

function randomFrom(seed) {
    let value = seed >>> 0;
    return () => {
        value = (Math.imul(value, 1664525) + 1013904223) >>> 0;
        return value / 0x100000000;
    };
}

function shuffle(items, random) {
    const result = [...items];
    for (let i = result.length - 1; i > 0; i--) {
        const j = Math.floor(random() * (i + 1));
        [result[i], result[j]] = [result[j], result[i]];
    }
    return result;
}

function handKey(input) {
    return input.main.slice(0, 5).sort((a, b) => a - b).join(',');
}

function makeSuite(deck, seen, seed, perStratum) {
    const random = randomFrom(seed);
    return Array.from({ length: 6 }, (_, count) => Array.from({ length: perStratum }, (_, index) => {
        let main;
        do main = shuffle(deck.main, random);
        while (seen.has(handKey({ main })));
        seen.add(handKey({ main }));
        const traps = shuffle(trapDeck, random);
        const hand = shuffle([...traps.slice(0, count), ...Array(5 - count).fill(blank)], random);
        const rest = shuffle([...traps.slice(count), ...Array(6 + count).fill(blank)], random);
        const draw_seed = Math.floor(random() * 2 ** 32);
        return { id: `${count}-${index}`, count, input: {
            main, extra: deck.extra, opponent: { main: [...hand, ...rest], extra: [] }, draw_seed,
        } };
    })).flat();
}

function readDeck(file) {
    const deck = { main: [], extra: [] };
    let section;
    for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
        if (line === '#main' || line === '#extra') section = line.slice(1);
        else if (line === '!side') section = undefined;
        else if (section && /^\d+$/.test(line)) deck[section].push(Number(line));
    }
    if (deck.main.length !== 40 || deck.extra.length !== 15)
        throw new Error('Expected a 40-card main and a 15-card extra deck');
    return deck;
}

function context(state) {
    const source = [...state.chains.values()].at(-1);
    return [
        `summons=${Math.min(state.summons, 5)}`, `draws=${Math.min(state.draws, 3)}`,
        `losses=${state.lost}`, `hint=${state.selectionHint || 0}`, `last=${state.lastIdle || 0}`,
        `chain=${source?.player ?? -1}/${source?.code ?? 0}`,
        ...[...new Set(state.board)].map(id => 'field=' + id),
        ...[...new Set(state.hand)].map(id => 'hand=' + id),
        ...[...new Set(state.grave)].map(id => 'grave=' + id),
        ...[...new Set(state.trapActions.map(action => action[0]))].map(id => 'hit=' + id),
    ];
}

function features(name, key, tags) {
    const action = name + ':' + key;
    return [action, ...tags.map(tag => action + '|' + tag)];
}

function value(weights, name, key, tags) {
    let score = 0;
    for (const feature of features(name, key, tags)) score += weights[feature] || 0;
    return score;
}

function fit(episodes) {
    const weights = Object.create(null);
    const rows = episodes.flatMap(episode => episode.decisions);
    for (let epoch = 0; epoch < 4; epoch++) {
        for (const row of shuffle(rows, randomFrom(1969 + epoch))) {
            const variants = row.options.filter(key => key !== row.chosen);
            if (!variants.length) continue;
            const ranked = variants.map(key => [value(weights, row.name, key, row.context), key]);
            ranked.sort((a, b) => b[0] - a[0]);
            if (value(weights, row.name, row.chosen, row.context) >= ranked[0][0] + 1) continue;
            const delta = 0.14 / Math.sqrt(1 + row.context.length);
            for (const sign of [1, -1]) {
                for (const key of features(row.name, sign === 1 ? row.chosen : ranked[0][1], row.context)) {
                    weights[key] = Math.max(-4, Math.min(4, (weights[key] || 0) + sign * delta));
                }
            }
        }
    }
    return { version: MODEL, weights, episodes: episodes.length, decisions: rows.length };
}

function execute(engine, scene, settings) {
    const { input, guide } = scene;
    const { model, seed, deadline, parent, pivot, collect, stop } = settings;
    const random = randomFrom(seed);
    const decisions = [];
    const result = rollout(engine.core, { ...input, trapCount: 0 }, ownPolicy(seed, true), random, deadline,
        parent, pivot ?? -1, engine.cardReader, {
            recordOccurrence: Boolean(guide),
            opponent: (msg, state, options) => opponentChoice(msg, state, options, scene.target),
            choose: ({ name, state, choices, step }) => {
                if (guide) {
                    const row = guide[step];
                    const found = choices.find(option => row?.name === name && row.response ===
                        Buffer.from(option.response).toString('base64'));
                    if (!found) throw new Error(`Guide diverged on ${scene.id} at ${step} (${name})`);
                    return found;
                }
                if (stop) {
                    if (name === 'SelectIdleCmd') return choices.find(option => option.key === 'end');
                    if (name === 'SelectChain') return choices.find(option => option.key === 'pass');
                    if (name === 'SelectEffectYn' || name === 'SelectYesNo')
                        return choices.find(option => option.key.endsWith(':false'));
                    return undefined;
                }
                if (!model || choices.length < 2) return undefined;
                const tags = context(state);
                const weighted = choices.map(choice => choice.weight *
                    Math.exp(Math.max(-7, Math.min(7, value(model.weights, name, choice.key, tags)))));
                let threshold = weighted.reduce((sum, x) => sum + x, 0) * random();
                return choices.find((_, index) => (threshold -= weighted[index]) < 0) || choices.at(-1);
            },
            observe: collect ? ({ name, state, choices, chosen }) => {
                const options = [...new Set(choices.map(option => option.key))];
                if (options.length > 1) decisions.push({ name, options, chosen: chosen.key, context: context(state) });
            } : undefined,
        });
    if (engine.engineErrors.size) {
        const errors = [...engine.engineErrors].slice(0, 4);
        engine.engineErrors.clear();
        throw new Error(errors.join('\n'));
    }
    if (result) {
        result.decisions = decisions;
        result.tiebreak = magistusTie(card, trap, { hand: result.hand,
            field: result.board.map(code => ({ code })), spells: result.spells.map(code => ({ code })),
            state: { events: result.events }, secondInterrupted: result.traps.length > 0 }, result);
    }
    return result;
}

function reward(result) {
    return adjusted(result);
}

function hasBoard(result) {
    return result.board.some(Boolean) || result.spells.length > 0;
}

function order(a, b) {
    const value = reward(a), difference = reward(b) - value;
    if (difference) return difference;
    if (value === 0) {
        const boardDifference = Number(hasBoard(b)) - Number(hasBoard(a));
        if (boardDifference) return boardDifference;
    }
    return a.draws - b.draws || compare(a, b) || (b.tiebreak?.score ?? 0) - (a.tiebreak?.score ?? 0);
}

function play(engine, scene, model, attempts, budget, branching, seed, mixRules = false) {
    const deadline = performance.now() + budget;
    const random = randomFrom(seed);
    const pool = [];
    let unsupported = 0, actual = 0;
    // Measure a legal pass instead of giving an unexplored line an assumed score.
    let stop_tested = false;
    try {
        const result = execute(engine, scene, { seed, deadline, stop: true });
        if (result && result.summons === 0) {
            result.stopped_immediately = true;
            stop_tested = true;
            pool.push(result);
        }
    } catch (error) {
        if (!(error instanceof UnsupportedPrompt)) throw error;
        unsupported++;
    }
    while (actual < attempts && performance.now() < deadline) {
        const parent = branching && actual % 3 ? pool[Math.floor(random() * pool.length)] : undefined;
        const pivots = parent?.steps.flatMap((step, i) => step.branch ? [i] : []);
        const pivot = pivots?.[Math.floor(random() * pivots.length)];
        actual++;
        try {
            const result = execute(engine, scene, {
                model: mixRules && actual % 6 === 0 && pivot === undefined ? null : model,
                seed: Math.floor(random() * 2 ** 32), deadline,
                parent: pivot === undefined ? undefined : parent, pivot, collect: branching,
            });
            if (result) {
                pool.push(result);
                pool.sort(order);
                if (pool.length > 6) pool.pop();
            }
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupported++;
        }
    }
    return { result: pool[0] || null, attempts: actual, unsupported, stop_tested };
}

function brief(run) {
    const summary = { attempts: run.attempts, unsupported: run.unsupported, stop_tested: run.stop_tested };
    if (!run.result) return { ...summary, result: null };
    const { score, lost, extra, draws, own_draws, board, summons, traps, nibiru_stopped,
        draw_phase_transforms, droll_access, little_knight } = run.result;
    return { ...summary, result: {
        score, lost, extra, draws, own_draws, board, summons, traps, nibiru_stopped,
        draw_phase_transforms, droll_access, little_knight, reward: reward(run.result),
        stopped_immediately: Boolean(run.result.stopped_immediately), has_board: hasBoard(run.result),
        tiebreak: run.result.tiebreak,
    } };
}

async function preflight(engine, deck) {
    for (const id of new Set([...deck.main, ...deck.extra, ...incoming.keys(), blank])) {
        const card = engine.cardReader(id);
        if (!card) throw new Error('Missing card data for ' + id);
        if (card.type !== 17) {
            const ids = new Set([id, card.alias || id]);
            let loaded = false;
            for (const code of ids) {
                if (await engine.scriptReader('c' + code + '.lua') ||
                    await engine.scriptReader('script/c' + code + '.lua')) loaded = true;
            }
            if (!loaded) throw new Error('Missing card script for ' + id);
        }
    }
}

function evaluate(engine, suite, model, incumbent, attempts, budget, deadline = Infinity) {
    const evidence = [];
    for (const [i, scene] of suite.entries()) {
        if (deadline - performance.now() < 2 * budget) return null;
        const seed = 92453 + i * 173;
        const before = brief(play(engine, scene, incumbent, attempts, budget, false, seed));
        const after = brief(play(engine, scene, model, attempts, budget, false, seed));
        evidence.push({ id: scene.id, count: scene.count, before, after });
    }
    return judge(suite, evidence);
}

function remember(seen, suite) {
    for (const scene of suite) {
        const key = handKey(scene.input);
        if (seen.has(key)) throw new Error('Opening overlap in archived inputs: ' + key);
        seen.add(key);
    }
}

function priorRun(folder, deck, seeds, seen, episodes) {
    const manifest = read(path.join(folder, 'manifest.json'));
    const hash = createHash('sha256').update(JSON.stringify(
        [MODEL, deck, seeds, manifest.attempts, manifest.budget, manifest.perStratum])).digest('hex');
    if (manifest.signature !== hash) throw new Error('Source checkpoint uses a different deck or seeds');
    remember(seen, read(path.join(folder, 'holdout.json')));
    const summaries = [];
    let incumbent = null;
    for (let round = 1; ; round++) {
        const dir = path.join(folder, 'round-' + round);
        const checkpoint = path.join(dir, 'summary.json');
        if (!fs.existsSync(checkpoint)) break;
        remember(seen, read(path.join(dir, 'inputs.json')));
        const summary = read(checkpoint);
        summaries.push(summary);
        episodes.push(...read(path.join(dir, 'episodes.json')));
        if (summary.evaluation.promote) incumbent = read(path.join(dir, 'candidate.json'));
    }
    if (!incumbent) throw new Error('A promoted model is required for extended training');
    return { incumbent, signature: hash, rounds: summaries.length,
        digest: createHash('sha256').update(JSON.stringify([summaries, incumbent])).digest('hex') };
}

function excludeRun(folder, seen) {
    const inputs = ['holdout.json', 'audit-inputs.json'].map(name => path.join(folder, name));
    for (const item of fs.readdirSync(folder, { withFileTypes: true })) {
        if (item.isDirectory() && /^round-\d+$/.test(item.name))
            inputs.push(path.join(folder, item.name, 'inputs.json'));
    }
    for (const file of inputs) {
        if (fs.existsSync(file)) remember(seen, read(file));
    }
    const model = path.join(folder, 'model.json');
    return fs.existsSync(model) ? read(model) : null;
}

function trainTimed(engine, deck, seeds, verified, output, source, settings) {
    const { attempts, budget, perStratum, seconds, gateSize, rounds } = settings;
    const started = Date.now(), deadline = performance.now() + seconds * 1000;
    const episodes = [...verified];
    const seen = new Set(seeds.map(seed => handKey(seed.input)));
    const previous = priorRun(source, deck, seeds, seen, episodes);
    const excludedModel = settings.excluded ? excludeRun(settings.excluded, seen) : null;
    const baseline = excludedModel || previous.incumbent;
    let incumbent = baseline;
    const signature = createHash('sha256').update(JSON.stringify(
        [previous.digest, settings, [...seen], baseline, DRAW_COST_TENTHS, SCORE_REVISION, SEARCH_REVISION,
            PROMOTION_RULE])).digest('hex');
    const file = name => path.join(output, name);
    const manifest = file('manifest.json');
    if (fs.existsSync(manifest) && read(manifest).signature !== signature)
        throw new Error('Source checkpoints or timed options changed. Use a new output directory.');
    write(manifest, { signature, source, sourceRounds: previous.rounds, settings,
        draw_cost: DRAW_COST_TENTHS / 10, score_revision: SCORE_REVISION, search_revision: SEARCH_REVISION,
        promotion_rule: PROMOTION_RULE });
    const gate = makeSuite(deck, seen, 375115, gateSize);
    const audit = makeSuite(deck, seen, 922047, gateSize);
    write(file('holdout.json'), gate);
    write(file('audit-inputs.json'), audit);

    let promoted = 0, completed = 0, searched = 0, retained = 0;
    let nextRound;
    for (nextRound = 1; nextRound <= rounds && performance.now() + 100 < deadline; nextRound++) {
        const roundDir = file('round-' + (previous.rounds + nextRound));
        fs.mkdirSync(roundDir, { recursive: true });
        const suite = makeSuite(deck, seen, 910057 + nextRound * 131, perStratum);
        const roundFile = name => path.join(roundDir, name);
        const checkpoint = roundFile('summary.json');
        if (fs.existsSync(checkpoint)) {
            const saved = read(checkpoint);
            if (saved.evaluation.promote) {
                incumbent = read(roundFile('candidate.json'));
                promoted++;
            }
            episodes.push(...read(roundFile('episodes.json')));
            searched += suite.length;
            retained += saved.fresh;
            completed++;
            continue;
        }
        write(roundFile('inputs.json'), suite);
        const partial = roundFile('partial.json');
        const { observations, fresh } = fs.existsSync(partial) ? read(partial) : { observations: [], fresh: [] };
        const save = () => write(partial, { observations, fresh });
        while (observations.length < suite.length && performance.now() + 100 < deadline) {
            const i = observations.length, scene = suite[i];
            const run = play(engine, scene, incumbent, attempts, Math.min(budget, deadline - performance.now()),
                true, 640719 + i * 23 + nextRound * 131, true);
            observations.push({ id: scene.id, count: scene.count, ...brief(run) });
            if (run.result && reward(run.result) > 0 && run.result.decisions.length) fresh.push({
                id: scene.id, count: scene.count, opening: handKey(scene.input),
                result: brief(run), decisions: run.result.decisions,
            });
            if (observations.length % Math.min(perStratum, 6) === 0) save();
        }
        save();
        searched += observations.length;
        retained += fresh.length;
        if (observations.length < suite.length || deadline - performance.now() < 2 * budget) break;
        const candidate = fit([...episodes, ...fresh]);
        write(roundFile('candidate.json'), candidate);
        const evaluation = evaluate(engine, gate, candidate, incumbent, Math.min(attempts, 12), budget, deadline);
        if (!evaluation) break;
        write(roundFile('episodes.json'), fresh);
        write(roundFile('observations.json'), observations);
        write(checkpoint, { round: previous.rounds + nextRound, episodes: episodes.length + fresh.length,
            fresh: fresh.length, evaluation });
        episodes.push(...fresh);
        if (evaluation.promote) {
            incumbent = candidate;
            promoted++;
        }
        write(file('model.json'), incumbent);
        completed++;
        console.log(JSON.stringify({ stage: 'evaluated', round: previous.rounds + nextRound,
            searched, retained, fresh: fresh.length, before: evaluation.before,
            after: evaluation.after, promoted: evaluation.promote }));
    }
    write(file('model.json'), incumbent);
    const stats = { started: new Date(started).toISOString(), training_ms: Date.now() - started,
        seconds, completed, searched, retained, promoted, nextRound: previous.rounds + nextRound };
    write(file('training-summary.json'), stats);
    console.log(JSON.stringify({ stage: 'training-finished', ...stats }));
    const final = evaluate(engine, audit, incumbent, baseline, Math.min(attempts, 12), budget);
    write(file('audit.json'), final);
    console.log(JSON.stringify({ stage: 'independent-audit', before: final.before, after: final.after,
        promote: final.promote }));
}

function verifySeeds(engine, seeds, deck) {
    return seeds.map(seed => {
        if (JSON.stringify([...seed.input.main].sort()) !== JSON.stringify([...deck.main].sort()) ||
            JSON.stringify(seed.input.extra) !== JSON.stringify(deck.extra))
            throw new Error('Wrong base deck for seed ' + seed.id);
        const result = execute(engine, seed, { seed: 1, deadline: performance.now() + 8000, collect: true });
        const { expected } = seed;
        if (!result || result.steps.length !== seed.guide.length ||
            ['score','lost','own_draws','draws','nibiru_stopped'].some(k => result[k] !== expected[k]) ||
            JSON.stringify(result.board) !== JSON.stringify(expected.board) ||
            JSON.stringify(result.traps) !== JSON.stringify(expected.traps))
            throw new Error('Seed outcome mismatch: ' + seed.id);
        return { id: seed.id, decisions: result.decisions, result: brief({ result }),
            liveSteps: result.steps.map(({ name, key, occurrence }) => ({ name, key, occurrence })) };
    });
}

async function main() {
    const { positionals, values: args } = parseArgs({
        allowPositionals: true,
        options: { fixture: { type: 'string' }, data: { type: 'string', multiple: true },
            deck: { type: 'string' }, seeds: { type: 'string' }, output: { type: 'string' },
            input: { type: 'string' }, model: { type: 'string' }, baseline: { type: 'string' }, rounds: { type: 'string' },
            attempts: { type: 'string' }, budget: { type: 'string' }, 'per-stratum': { type: 'string' },
            'from-run': { type: 'string' }, seconds: { type: 'string' }, 'gate-per-stratum': { type: 'string' },
            'exclude-run': { type: 'string' } },
    });
    const [mode] = positionals;
    if (!['train', 'play', 'verify', 'audit'].includes(mode) || !args.fixture || !args.data?.length || !args.deck)
        throw new Error('Usage: offline.cjs train|play|verify|audit --fixture DIR --data DIR [--data OVERLAY] --deck FILE');
    const bounded = (key, fallback) => {
        const n = Number(args[key] ?? fallback);
        if (!Number.isInteger(n) || n <= 0) throw new Error('Expected a positive integer for --' + key);
        return n;
    };
    const deck = readDeck(args.deck);
    const engine = await loadEngine(path.resolve(args.fixture), args.data.map(p => path.resolve(p)));
    await preflight(engine, deck);
    const budget = bounded('budget', 2800), attempts = bounded('attempts', 28);
    if (mode === 'audit') {
        const suite = read(args.input);
        const model = read(args.model);
        const baseline = args.baseline ? read(args.baseline) : null;
        if (model.version !== MODEL || (baseline && baseline.version !== MODEL))
            throw new Error('Unsupported model format');
        const audit = evaluate(engine, suite, model, baseline, attempts, budget);
        const summary = { ...audit, search_revision: SEARCH_REVISION };
        if (args.output) write(path.resolve(args.output), summary);
        console.log(JSON.stringify({ before: audit.before, after: audit.after,
            draw_trap: audit.draw_trap, promote: audit.promote }));
        return;
    }
    if (mode === 'play') {
        const model = args.model ? read(args.model) : null;
        if (model && model.version !== MODEL) throw new Error('Unsupported model format');
        // A play input supplies two real, preordered decks, the first five cards and RNG seed.
        const input = read(args.input);
        console.log(JSON.stringify(brief(play(engine, { input }, model, attempts, budget, false, input.draw_seed))));
        return;
    }

    const seeds = read(args.seeds);
    const verified = verifySeeds(engine, seeds, deck);
    console.log(JSON.stringify({ stage: 'verified-seeds', count: verified.length,
        unique_hands: new Set(seeds.map(seed => handKey(seed.input))).size }));
    if (mode === 'verify') {
        if (args.output) write(path.resolve(args.output), verified.map((sample, i) => {
            const { input, expected } = seeds[i];
            const fillers = new Set(input.preserveOpening || []);
            return { id: sample.id, core: input.main.slice(0, 5).filter(card => !fillers.has(card)),
                pressure: expected.traps[0]?.[0] || 0, score: expected.score, steps: sample.liveSteps };
        }));
        return;
    }

    const output = path.resolve(args.output);
    fs.mkdirSync(output, { recursive: true });
    const rounds = bounded('rounds', 2), perStratum = bounded('per-stratum', 4);
    if (args['from-run'] || args.seconds) {
        if (!args['from-run'] || !args.seconds || path.resolve(args['from-run']) === output)
            throw new Error('Timed training requires --seconds and a distinct --from-run directory');
        trainTimed(engine, deck, seeds, verified, output, path.resolve(args['from-run']), {
            attempts, budget, perStratum, rounds, seconds: bounded('seconds', 2700),
            gateSize: bounded('gate-per-stratum', 4),
            ...(args['exclude-run'] && { excluded: path.resolve(args['exclude-run']) }),
        });
        return;
    }
    const signature = createHash('sha256').update(JSON.stringify([MODEL, deck, seeds, attempts, budget, perStratum]))
        .digest('hex');
    const manifest = path.join(output, 'manifest.json');
    if (fs.existsSync(manifest) && read(manifest).signature !== signature)
        throw new Error('Deck, seeds or search configuration changed. Use a new output directory.');
    write(manifest, { signature, attempts, budget, perStratum, seed_count: verified.length });
    let incumbent = null;
    const episodes = [...verified];
    const seen = new Set(seeds.map(seed => handKey(seed.input)));
    const holdout = makeSuite(deck, seen, 44107, 2);
    write(path.join(output, 'holdout.json'), holdout);
    write(path.join(output, 'seed-model.json'), fit(verified));
    for (let round = 1; round <= rounds; round++) {
        const roundDir = path.join(output, 'round-' + round);
        fs.mkdirSync(roundDir, { recursive: true });
        const suite = makeSuite(deck, seen, 86021 + round * 29, perStratum);
        const checkpoint = path.join(roundDir, 'summary.json');
        if (fs.existsSync(checkpoint)) {
            const saved = read(checkpoint);
            if (JSON.stringify(saved.config) !== JSON.stringify({ attempts, budget, perStratum }))
                throw new Error('Cannot resume with different search parameters');
            episodes.push(...read(path.join(roundDir, 'episodes.json')));
            if (saved.evaluation.promote) incumbent = read(path.join(roundDir, 'candidate.json'));
            console.log(JSON.stringify({ stage: 'resumed', round, promoted: saved.evaluation.promote }));
            continue;
        }
        write(path.join(roundDir, 'inputs.json'), suite);
        const teacher = incumbent || fit(episodes);
        const fresh = [];
        const observations = [];
        for (const [i, scene] of suite.entries()) {
            const run = play(engine, scene, teacher, attempts, budget, true, 25619 + i * 23);
            observations.push({ id: scene.id, count: scene.count, ...brief(run) });
            if (run.result?.score > 0 && run.result.decisions.length) fresh.push({
                id: scene.id, count: scene.count, opening: handKey(scene.input),
                result: brief(run), decisions: run.result.decisions,
            });
            if ((i + 1) % perStratum === 0) {
                write(path.join(roundDir, 'observations.json'), observations);
                console.log(JSON.stringify({ stage: 'searched', round, processed: i + 1, retained: fresh.length }));
            }
        }
        episodes.push(...fresh);
        write(path.join(roundDir, 'episodes.json'), fresh);
        const candidate = fit(episodes);
        write(path.join(roundDir, 'candidate.json'), candidate);
        const evaluation = evaluate(engine, holdout, candidate, incumbent, Math.min(attempts, 10), budget);
        if (evaluation.promote) {
            incumbent = candidate;
            write(path.join(output, 'model.json'), incumbent);
        }
        write(checkpoint, { round, episodes: episodes.length, fresh: fresh.length,
            config: { attempts, budget, perStratum }, evaluation });
        console.log(JSON.stringify({ stage: 'evaluated', round, fresh: fresh.length,
            before: evaluation.before, after: evaluation.after, promoted: evaluation.promote }));
    }
}

module.exports = { readDeck, context, value, MODEL, randomFrom, shuffle, trapDeck };

if (require.main === module) {
    main().catch(error => { console.error(error.stack); process.exitCode = 1; });
}
