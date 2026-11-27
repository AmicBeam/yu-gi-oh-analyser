'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { parseArgs } = require('node:util');
const { Worker, isMainThread, parentPort, workerData } = require('node:worker_threads');
const P = require('ygopro-msg-encode');
const { initialDuel, field, buildPairs, compare } = require('../experiment/second-turn-search.cjs');
const { engineOptions, nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { card, trap, drawTraps, settledScore } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');
const { readDeck, randomFrom, shuffle, trapDeck } = require('./offline.cjs');
const { MagistusPolicy } = require('./live-policy.cjs');
const { magistusTie } = require('../turn-evidence.cjs');
const { turnAxis, verifyTiedRoutes } = require('../turn-objective.cjs');
const { VERSION } = require('./second-turn.cjs');

const BOT = 1, OPPONENT = 0, MZONE = 4, SZONE = 8, MAIN1 = 4, MAIN2 = 256;
const handTraps = new Set(trapDeck);
const setCards = [[1, 98360333], [2, 25156], [4, 65681983], [8, trap.imperm]];
const mirror = [
    { board: [card.savage], traps: 8 },
    { board: [card.savage, card.dragon, card.four], traps: 8 },
    { board: [card.littleKnight, card.moon], traps: 8 },
    { board: [card.dragon], traps: 0 },
];
const wireKey = response => Buffer.from(response).toString('base64');
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));

function write(file, object) {
    fs.writeFileSync(file + '.tmp', JSON.stringify(object) + '\n');
    fs.renameSync(file + '.tmp', file);
}

function makeCases(options, deck, boards, count, mirrored, offset, generic = []) {
    const random = randomFrom(options.seed + offset);
    const result = [], seen = new Set();
    const fillers = Array(11).fill(89631139);
    for (let index = 0; index < count; index++) {
        let main, key;
        do {
            main = shuffle(deck.main, random);
            key = main.slice(0, 6).sort((a, b) => a - b).join(',');
        } while (seen.has(key));
        seen.add(key);
        const type = generic.length && index % 7 < 4 ? 'generic' : index % 2 ? 'twins' : 'mirror';
        const short = index % 5 === 0;
        const pool = type === 'generic' ? generic : type === 'twins' ? boards : mirrored;
        const reduced = pool.filter(row => row.board.filter(Boolean).length + (row.set?.length ?? Number(Boolean(row.traps))) <= 2);
        const source = (short && reduced.length) ? reduced : pool;
        const row = source[Math.floor(random() * source.length)];
        const opponentField = row.board.flatMap((code, sequence) => code ?
            [{ code, sequence, location: MZONE, position: row.positions?.[sequence] || 1 }] : []);
        const backrow = row.set || setCards.flatMap(([bit, code]) => row.traps & bit ? [code] : []);
        opponentField.push(...backrow.map((code, sequence) => ({ code, sequence, location: SZONE, position: 8 })));
        result.push({ id: index + offset, type, main, extra: deck.extra, seed: Math.floor(random() * 2 ** 32),
            field: opponentField, opponent_lp: short ? [1200, 1800, 2800][index % 3] : 8000,
            opponent: { main: shuffle([...trapDeck, ...fillers], random), extra: [] } });
    }
    return result;
}

async function verifyResources(config, deck, boards) {
    const { cardReader, scriptReader } = await loadEngine(config.fixture, config.data);
    const ids = new Set([...deck.main, ...deck.extra, ...trapDeck, 89631139, ...setCards.map(([, id]) => id),
        ...boards.concat(mirror).flatMap(row => [...row.board, ...(row.set || [])]).filter(Boolean)]);
    const missing = [];
    for (const id of ids) {
        const info = cardReader(id);
        if (!info) missing.push(`card ${id}`);
        else if (!(info.type & 0x10) && !scriptReader(`./script/c${id}.lua`)) missing.push(`script c${id}.lua`);
    }
    if (missing.length) throw new Error('Missing matchup resources: ' + missing.join(', '));
}

function enemy(msg, policy, tried) {
    const name = nameOf(msg);
    if (name === 'SelectIdleCmd') return msg.prepareResponse(P.IdleCmdType.TO_EP);
    if (name === 'SelectChain') {
        const last = [...policy.state.chains.values()].at(-1);
        const choice = msg.chains.find(c => c.forced) || (policy.state.turn === 2 && msg.chains.find(c =>
            !tried.has(c.code) && (
                drawTraps.has(c.code) ||
                (c.code === trap.nibiru && policy.state.summons >= 5) ||
                (c.location === SZONE && policy.state.phase >= 8 && policy.state.phase <= 128) ||
                (last?.player === 0 && (handTraps.has(c.code) || c.location === MZONE || c.location === SZONE))
            )));
        if (choice) tried.add(choice.code);
        return msg.prepareResponse(choice || undefined);
    }
    if (name === 'SelectCard') {
        const candidates = [...msg.cards].sort((a, b) => Number(b.controller === BOT) - Number(a.controller === BOT));
        return msg.prepareResponse(candidates.slice(0, msg.min));
    }
    if (name === 'SelectEffectYn' || name === 'SelectYesNo') return msg.prepareResponse(false);
    try {
        return engineOptions(msg, policy.state, policy.state.searchPolicy)[0]?.response ?? msg.defaultResponse();
    } catch (error) {
        if (!(error instanceof UnsupportedPrompt)) throw error;
        return msg.defaultResponse();
    }
}

function sample(choices, random) {
    const ceiling = Math.max(...choices.map(o => o.score));
    const weights = choices.map(o => Math.exp((o.score - ceiling) * 0.65));
    let ticket = random() * weights.reduce((a, b) => a + b, 0);
    return choices.find((o, i) => (ticket -= weights[i]) < 0) || choices.at(-1);
}

function refresh(duel, policy, name) {
    const locations = name === 'SelectBattleCmd' || name === 'SelectIdleCmd' ? [MZONE, SZONE] : [MZONE];
    for (const player of [BOT, OPPONENT]) for (const location of locations) {
        const { cards } = duel.queryFieldCard({ player, location, queryFlag: 0xf81fff, useCache: 0 });
        if (player === OPPONENT) {
            if (location === MZONE) {
                policy.enemyField = cards.map(c => c.code && (c.position & 5) ? c : undefined);
                policy.enemyMonsters = cards.map(c => Boolean(c.code));
            }
            else policy.enemyBackrow = cards.map(c => Boolean(c.code));
        } else if (location === MZONE) policy.field = cards.map(c => c.code ? c : undefined);
        else policy.spells = cards.map(c => c.code ? c : undefined);
    }
}

function run(engine, input, options) {
    const duel = initialDuel(engine.core, input);
    const policy = new MagistusPolicy(BOT, engine.cardReader, options.guides, options.model, () => {},
        input.extra, options.second);
    policy.lp[OPPONENT] = input.opponent_lp;
    const tried = new Set(), wire = [], steps = [];
    let initial = input.field.length, cleared, damage = 0, effects = 0, resolving;
    const random = options.random;
    if (options.intervene || random) policy.exploreSecond = (name, choices) => {
        const offset = wire.length;
        if (options.intervene) {
            const ordered = [...choices].sort((a, b) => b.score - a.score);
            const requested = options.intervene.get(offset);
            const choice = requested === undefined ? ordered[0] : ordered.find(o => wireKey(o.response) === requested);
            if (!choice) return ordered[0];
            steps.push({ offset, features: choice.keys, action: wireKey(choice.response),
                alternatives: ordered.slice(1, 4).map(o => wireKey(o.response)) });
            return choice;
        }
        const parent = options.parent, pivot = options.pivot;
        const candidates = parent && offset === pivot ? choices.filter(o => wireKey(o.response) !== parent.wire[offset]) : choices;
        const choice = sample(candidates.length ? candidates : choices, random);
        steps.push({ offset, prefix: createHash('sha256').update(wire.join('|')).digest('hex').slice(0, 20),
            action: wireKey(choice.response), features: choice.keys });
        return choice;
    };
    const terminal = win => {
        const remaining = field(duel, OPPONENT).length;
        const own = field(duel, BOT, 0xfbdfff);
        const spells = own.filter(c => c.location === SZONE).map(c => ({
            ...c, owner: c.owner === BOT ? 0 : 1,
            equipCard: c.equipCard && { ...c.equipCard, controller: c.equipCard.controller === BOT ? 0 : 1 },
        }));
        const board = settledScore(own.filter(c => c.location === MZONE), policy.state.lost, 0, policy.state.draws, spells);
        const tiebreak = magistusTie(card, trap, policy, board);
        const retained = board.score - 5 * policy.state.draws;
        const removed = initial - remaining;
        return { outcome: [win, removed, damage, retained, tiebreak.score, 10 * removed + retained],
            tiebreak, main2: cleared !== undefined, interrupted: policy.secondInterrupted, effects, steps, wire };
    };
    try {
        for (let i = 0; i < 1200 && Date.now() < options.deadline; i++) {
            for (const msg of duel.process().messages || []) {
                const name = nameOf(msg);
                if (name === 'Retry') return null;
                if (name === 'NewTurn' && policy.state.turn === 2) return terminal(0);
                if (name === 'Win') return terminal(msg.player === BOT ? 1 : msg.player === OPPONENT ? -1 : 0);
                policy.observe(msg);
                if (name === 'ChainSolving') resolving = policy.state.solving;
                if (name === 'ChainSolved' || name === 'ChainEnd') resolving = undefined;
                if (policy.state.turn === 2) {
                    if (name === 'NewPhase' && msg.phase === MAIN1) initial = field(duel, OPPONENT).length;
                    if (name === 'NewPhase' && msg.phase === MAIN2) cleared = initial - field(duel, OPPONENT).length;
                    if (name === 'Damage' && msg.player === OPPONENT && cleared === undefined) damage += msg.value;
                    if (name === 'Move' && msg.previous.controller === OPPONENT &&
                        [MZONE, SZONE].includes(msg.previous.location) &&
                        (msg.current.controller !== OPPONENT || ![MZONE, SZONE].includes(msg.current.location)) &&
                        resolving?.player === 0) effects++;
                }
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                const replay = options.parent?.wire[wire.length];
                let response;
                if (replay !== undefined && wire.length < options.pivot) {
                    response = Buffer.from(replay, 'base64');
                    if (msg.responsePlayer() === OPPONENT && name === 'SelectChain') {
                        const c = msg.chains.find(ch => wireKey(msg.prepareResponse(ch)) === replay);
                        if (c) tried.add(c.code);
                    }
                } else if (msg.responsePlayer() === BOT) {
                    refresh(duel, policy, name);
                    response = policy.choose(msg.playerView(BOT));
                } else response = enemy(msg, policy, tried);
                if (!response) return null;
                wire.push(wireKey(response));
                duel.setResponse(response);
            }
        }
        return null;
    } catch (error) {
        if (error instanceof UnsupportedPrompt) return { unsupported: error.prompt };
        throw error;
    } finally {
        duel.endDuel();
    }
}

function repair(engine, input, config, deadline) {
    deadline = Math.min(deadline, Date.now() + config.caseMs);
    const second = { weights: config.incumbent?.weights || {}, training: 'rule-continuation', combat: config.combat };
    const play = intervene => run(engine, input, { ...config, second, intervene, deadline });
    const reference = play(new Map()), pairs = [], runs = [];
    let attempts = 1;
    if (reference?.outcome) runs.push(reference);
    const ties = [];
    const random = randomFrom(input.seed);
    const frontier = shuffle((reference?.steps || []).filter(s => s.alternatives.length), random).slice(0, 12);
    for (const step of frontier) for (const action of step.alternatives) {
        if (attempts >= config.attempts || Date.now() >= deadline) break;
        attempts++;
        const branch = play(new Map([[step.offset, action]]));
        const taken = branch?.steps?.find(s => s.offset === step.offset);
        if (!branch?.outcome || taken?.action !== action ||
            reference.wire.slice(0, step.offset).some((r, i) => r !== branch.wire[i])) continue;
        branch.override = [step.offset, action];
        runs.push(branch);
        const sign = compare(branch.outcome, reference.outcome);
        if (!sign) continue;
        const axis = turnAxis(branch.outcome, reference.outcome);
        const high = sign > 0 ? taken.features : step.features, low = sign > 0 ? step.features : taken.features;
        if (high.join('|') === low.join('|')) continue;
        const prefix = createHash('sha256').update(reference.wire.slice(0, step.offset).join('|')).digest('hex').slice(0, 20);
        const pair = { prefix, high, low, axis };
        if (axis === 4) ties.push({ branch, sign, pair });
        else pairs.push(pair);
    }
    verifyTiedRoutes(runs, route => run(engine, input, { ...config, second, deadline: Date.now() + 350,
        intervene: new Map(route.override ? [route.override] : []) }));
    for (const { branch, sign, pair } of ties) if (reference.tieVerified && branch.tieVerified)
        pairs.push({ ...pair, tiebreak: { high: (sign > 0 ? branch : reference).tiebreak,
            low: (sign > 0 ? reference : branch).tiebreak } });
    const best = [...runs].sort((a, b) => compare(b.outcome, a.outcome))[0];
    const replay = best && run(engine, input, { ...config, second, deadline: Date.now() + 1200,
        intervene: new Map(best.override ? [best.override] : []) });
    const verified = Boolean(replay?.outcome && best.outcome.every((n, i) => n === replay.outcome[i]) &&
        best.main2 === replay.main2 && best.wire.length === replay.wire.length &&
        best.wire.every((value, i) => value === replay.wire[i]));
    return { attempts, settled: runs.length, wins: runs.filter(r => r.outcome[0] === 1).length,
        main2: runs.filter(r => r.main2).length, interrupted: runs.filter(r => r.interrupted).length,
        effects: runs.filter(r => r.effects).length, verified: Number(verified),
        unsupported: reference?.unsupported ? { [reference.unsupported]: 1 } : {},
        best: verified ? best.outcome : null, tiebreak: verified ? best.tiebreak : undefined, pairs: verified ? shuffle(pairs, random).slice(0, 12) : [] };
}

function search(engine, input, config, deadline) {
    deadline = Math.min(deadline, Date.now() + config.caseMs);
    const random = randomFrom(input.seed), runs = [], unsupported = {};
    let pool = [], attempts = 0;
    for (; attempts < config.attempts && Date.now() < deadline; attempts++) {
        const parent = attempts % 5 ? pool[Math.floor(random() ** 2 * pool.length)] : undefined;
        const pivot = parent?.steps[Math.floor(random() * parent.steps.length)]?.offset;
        const result = run(engine, input, { ...config, random, deadline, parent, pivot });
        if (result?.unsupported) unsupported[result.unsupported] = (unsupported[result.unsupported] || 0) + 1;
        else if (result?.outcome) {
            runs.push(result);
            if (result.steps.length) pool = [...pool, result].sort((a, b) => compare(b.outcome, a.outcome)).slice(0, 24);
        }
    }
    const best = runs.sort((a, b) => compare(b.outcome, a.outcome))[0];
    verifyTiedRoutes(runs, route => run(engine, input, { ...config, deadline: Date.now() + 350,
        parent: route, pivot: Infinity }));
    const replay = best && run(engine, input, { ...config, deadline: Date.now() + 2000, parent: best, pivot: Infinity });
    const verified = Boolean(replay?.outcome && best.outcome.every((n, i) => n === replay.outcome[i]));
    return { attempts, settled: runs.length, wins: runs.filter(r => r.outcome[0] === 1).length,
        main2: runs.filter(r => r.main2).length, interrupted: runs.filter(r => r.interrupted).length,
        effects: runs.filter(r => r.effects).length, verified: Number(verified), unsupported,
        best: verified ? best.outcome : null, tiebreak: verified ? best.tiebreak : undefined, pairs: verified ? shuffle(buildPairs(runs), random).slice(0, 180) : [] };
}

function fit(weights, pairs) {
    for (const { high, low, axis } of pairs) {
        const delta = new Map();
        for (const key of high) delta.set(key, (delta.get(key) || 0) + 1);
        for (const key of low) delta.set(key, (delta.get(key) || 0) - 1);
        const margin = [...delta].reduce((sum, [key, v]) => sum + v * (weights[key] || 0), 0);
        const step = (0.035 + 0.005 * (3 - axis)) / (1 + Math.exp(Math.max(-30, Math.min(30, margin))));
        for (const [key, v] of delta) if (v) weights[key] = Math.max(-3, Math.min(3, (weights[key] || 0) + v * step));
    }
}

async function worker(config) {
    const engine = await loadEngine(config.fixture, config.data);
    if (config.audit) {
        const rows = config.cases.map(input => {
            const play = second => {
                const r = run(engine, input, { ...config, second, deadline: Date.now() + 2500 });
                return r?.outcome ? { outcome: r.outcome, main2: r.main2, interrupted: r.interrupted } : null;
            };
            return { id: input.id, type: input.type, baseline: play(undefined), prior: play({ weights: {} }),
                ...(config.combat && { combatOnly: play({ weights: {}, combat: config.combat }) }),
                ...(config.incumbent && { incumbent: play({ ...config.incumbent, combat: config.combat }) }),
                ...Object.fromEntries(Object.entries(config.candidates).map(([key, model]) => [key, play(model)])) };
        });
        parentPort.postMessage({ audit: rows });
        return;
    }
    for (const input of config.cases) {
        if (Date.now() >= config.deadline) break;
        const train = config.repair ? repair : search;
        parentPort.postMessage({ id: input.id, type: input.type, ...train(engine, input, config, config.deadline) });
    }
}

function pool(config, cases, processRow) {
    return Promise.all(Array.from({ length: config.jobs }, (_, index) => new Promise((resolve, reject) => {
        const child = new Worker(__filename, { workerData: { ...config,
            cases: cases.filter((_, i) => i % config.jobs === index) } });
        child.on('message', processRow);
        child.on('error', reject);
        child.on('exit', code => code ? reject(new Error('Training worker exited ' + code)) : resolve());
    })));
}

async function main() {
    const { values: args } = parseArgs({ options: {
        fixture: { type: 'string' }, data: { type: 'string', multiple: true }, boards: { type: 'string' },
        'generic-boards': { type: 'string' },
        'audit-generic-boards': { type: 'string' }, repair: { type: 'boolean' }, 'combat-plan': { type: 'boolean' },
        'continuation-base': { type: 'string' },
        model: { type: 'string' }, output: { type: 'string' }, seconds: { type: 'string', default: '1800' },
        jobs: { type: 'string', default: '4' }, attempts: { type: 'string', default: '96' },
        'case-ms': { type: 'string', default: '6500' }, 'max-cases': { type: 'string', default: '6000' },
        holdout: { type: 'string', default: '40' }, seed: { type: 'string', default: '2026112702' },
    } });
    if (!args.fixture || !args.data || !args.boards || !args.model || !args.output)
        throw new Error('Required: --fixture --data --boards --model --output');
    const seconds = Number(args.seconds), output = path.resolve(args.output);
    const config = { fixture: path.resolve(args.fixture), data: args.data.map(p => path.resolve(p)),
        model: read(args.model), guides: read(path.join(__dirname, 'live-guides.json')),
        attempts: Number(args.attempts), jobs: Number(args.jobs), seed: Number(args.seed), caseMs: Number(args['case-ms']),
        repair: Boolean(args.repair), combat: args['combat-plan'] ? 'public-plan' : undefined,
        incumbent: args['continuation-base'] ? read(args['continuation-base']) : undefined };
    if (config.incumbent && (!config.repair || config.incumbent.version !== VERSION ||
        config.incumbent.training !== 'rule-continuation' || !config.incumbent.weights))
        throw new Error('Continuation base must be a compatible repair model');
    if (![seconds, config.attempts, config.jobs, config.caseMs, Number(args.holdout), Number(args['max-cases'])]
        .every(n => Number.isInteger(n) && n > 0)) throw new Error('Training bounds must be positive integers');
    fs.mkdirSync(output, { recursive: true });
    const deck = readDeck(path.join(__dirname, 'AI_Magistus.ydk'));
    const boards = read(args.boards);
    const generic = args['generic-boards'] ? read(args['generic-boards']) : [];
    const auditGeneric = args['audit-generic-boards'] ? read(args['audit-generic-boards']) : generic;
    if (!boards.length) throw new Error('The opposing board pool is empty');
    await verifyResources(config, deck, [...boards, ...generic, ...auditGeneric]);
    const audit = makeCases(config, deck, boards, Number(args.holdout) * 2, mirror, 100000, auditGeneric);
    const held = new Set(audit.map(c => c.main.slice(0, 6).sort((a,b) => a-b).join(',')));
    const cases = makeCases(config, deck, boards, Number(args['max-cases']), mirror, 0, generic)
        .filter(c => !held.has(c.main.slice(0, 6).sort((a,b) => a-b).join(',')));
    const weights = Object.create(null), stats = { cases: 0, attempts: 0, settled: 0, wins: 0, main2: 0,
        interrupted: 0, effects: 0, verified: 0, pairs: 0, unsupported: {} };
    const started = Date.now();
    config.deadline = started + seconds * 1000;
    const summary = (status, extra = {}) => {
        const value = { status, method: config.repair ? 'rule-continuation' : 'wide-search', combat: config.combat,
            genericBoards: generic.length,
            heldoutGenericBoards: auditGeneric.length, started: new Date(started).toISOString(), deadline: new Date(config.deadline).toISOString(),
            ...stats, ...extra };
        write(path.join(output, 'summary.json'), value);
        console.log(JSON.stringify(value));
    };
    const witnessLog = path.join(output, 'cases.jsonl');
    fs.writeFileSync(witnessLog, '');
    summary('running');
    await pool(config, cases, ({ pairs, unsupported, ...row }) => {
        stats.cases++;
        for (const key of ['attempts', 'settled', 'wins', 'main2', 'interrupted', 'effects', 'verified']) stats[key] += row[key];
        stats.pairs += pairs.length;
        for (const [prompt, count] of Object.entries(unsupported))
            stats.unsupported[prompt] = (stats.unsupported[prompt] || 0) + count;
        fit(weights, pairs);
        fs.appendFileSync(witnessLog, JSON.stringify({ ...row, pairs: pairs.length }) + '\n');
        if (stats.cases % 10 === 0) summary('running');
    });
    if (!stats.pairs) throw new Error('No verified training pairs');
    const model = strength => {
        const result = { ...config.incumbent?.weights };
        for (const [key, v] of Object.entries(weights))
            result[key] = Math.round(((result[key] || 0) + v * strength) * 1e6) / 1e6;
        return { version: VERSION, combat: config.combat, ...(config.repair && { training: 'rule-continuation' }), weights: result };
    };
    const candidates = config.repair ? { light: model(0.1), mild: model(0.25), medium: model(0.5) }
        : { mild: model(0.25), medium: model(0.5), full: model(1) };
    summary('auditing');
    const verify = async (candidates, cases) => {
        const result = [];
        await pool({ ...config, audit: true, candidates }, cases, message => result.push(...message.audit));
        return result.sort((a, b) => a.id - b.id);
    };
    const tune = await verify(candidates, audit.slice(0, Number(args.holdout)));
    const tuneRank = key => {
        if (config.repair) {
            const complete = tune.filter(r => r[key]).length;
            const gaps = tune.filter(r => (r.prior?.outcome[0] === 1 || r.incumbent?.outcome[0] === 1) &&
                r[key]?.outcome[0] !== 1).length;
            const wins = tune.filter(r => r[key]?.outcome[0] === 1).length;
            const progress = tune.reduce((n, r) => n + (!r[key] ? -2 : !r.prior ? 1 :
                Math.sign(compare(r[key].outcome, r.prior.outcome))), 0);
            return complete * 100000 - gaps * 10000 + wins * 100 + progress;
        }
        const wins = tune.filter(r => r[key]?.outcome[0] === 1).length;
        const progress = tune.reduce((n, r) => n + (!r[key] ? -2 : !r.prior ? 1 :
            Math.sign(compare(r[key].outcome, r.prior.outcome))), 0);
        return wins * (2 * tune.length + 1) + progress;
    };
    const selected = Object.keys(candidates).sort((a, b) => tuneRank(b) - tuneRank(a))[0];
    write(path.join(output, 'tune.json'), tune);
    write(path.join(output, 'second-turn-ranker.json'), candidates[selected]);
    const rows = await verify({ trained: candidates[selected] }, audit.slice(Number(args.holdout)));
    write(path.join(output, 'audit.json'), rows);
    const quality = (key, group = rows) => ({
        settled: group.filter(r => r[key]).length, wins: group.filter(r => r[key]?.outcome[0] === 1).length,
        clears: group.reduce((sum, r) => sum + (r[key]?.outcome[1] || 0), 0),
        damage: group.reduce((sum, r) => sum + (r[key]?.outcome[2] || 0), 0),
        main2: group.filter(r => r[key]?.main2).length,
    });
    const baseline = quality('baseline'), prior = quality('prior'), trained = quality('trained');
    const beats = key => rows.filter(r => r[key] && r.trained && compare(r.trained.outcome, r[key].outcome) > 0).length;
    const loses = key => rows.filter(r => r[key] && (!r.trained || compare(r.trained.outcome, r[key].outcome) < 0)).length;
    const gain = { baseline: [beats('baseline'), loses('baseline')], prior: [beats('prior'), loses('prior')],
        ...(config.combat && { combatOnly: [beats('combatOnly'), loses('combatOnly')] }),
        ...(config.incumbent && { incumbent: [beats('incumbent'), loses('incumbent')] }) };
    const genericRows = rows.filter(r => r.type === 'generic');
    const genericPrior = quality('prior', genericRows), genericTrained = quality('trained', genericRows);
    const lostPriorWins = rows.filter(r => (r.prior?.outcome[0] === 1 || r.incumbent?.outcome[0] === 1) &&
        r.trained?.outcome[0] !== 1).length;
    const missedMain2 = rows.filter(r => (r.prior?.main2 || r.incumbent?.main2) &&
        r.trained?.outcome[0] !== 1 && !r.trained?.main2).length;
    const genericDelta = genericRows.reduce((delta, row) => delta +
        (row.prior && row.trained ? Math.sign(compare(row.trained.outcome, row.prior.outcome)) : 0), 0);
    const promoted = trained.settled >= baseline.settled && trained.settled >= prior.settled &&
        trained.wins >= baseline.wins && trained.wins >= prior.wins &&
        gain.baseline[0] > gain.baseline[1] && gain.prior[0] > gain.prior[1] &&
        (!config.incumbent || gain.incumbent[0] > gain.incumbent[1]) &&
        (!config.repair || (genericTrained.settled >= genericPrior.settled &&
            genericTrained.wins >= genericPrior.wins && genericDelta >= 0 && lostPriorWins === 0 && missedMain2 === 0));
    summary('completed', { selected, baseline, prior, trained, gain, lostPriorWins, missedMain2,
        ...(config.combat && { combatOnly: quality('combatOnly') }),
        ...(config.incumbent && { incumbent: quality('incumbent') }),
        genericHoldout: { cases: genericRows.length, prior: genericPrior, trained: genericTrained, delta: genericDelta }, promoted });
}

if (require.main === module) (isMainThread ? main() : worker(workerData)).catch(error => {
    console.error(error.stack);
    process.exitCode = 1;
});

module.exports = { makeCases, mirror, run, search };
