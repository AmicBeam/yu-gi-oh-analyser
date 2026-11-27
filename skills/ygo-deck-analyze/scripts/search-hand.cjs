'use strict';

const readline = require('node:readline');
const path = require('node:path');
const P = require('ygopro-msg-encode');
const coreApi = require('koishipro-core.js');
const { engineOptions, nameOf, openDuel, selections, UnsupportedPrompt } =
    require('../../../ygo-windbot/tests/engine-search-protocol.cjs');
const { card, trap, drawTraps, ownDefensiveEffects, settledScore, ownPolicy, opponentChoice } =
    require('./handrip-profile.cjs');

const code = c => c.alias || c.code;
const query = (duel, location, player, flags = 0x0a400f) =>
    duel.queryFieldCard({ player, location, queryFlag: flags, useCache: 0 }).cards.filter(c => c.code);
const onField = location => location === 4 || location === 8;
const pick = (options, random) => {
    let total = options.reduce((sum, option) => sum + option.weight, 0) * random();
    return options.find(option => (total -= option.weight) < 0) || options.at(-1);
};
const keyOf = response => Buffer.from(response).toString('base64');

function optionsFor(msg, state) {
    const name = nameOf(msg);
    if (name === 'AnnounceAttrib') {
        const bits = [1, 2, 4, 8, 16, 32, 64].filter(bit => msg.availableAttributes & bit);
        return Array.from(selections(bits, msg.count, msg.count), group => {
            const mask = group.reduce((value, bit) => value | bit, 0);
            const seen = state.events.reduce((value, event) => {
                if (event[0] !== 'effect' || event[2] !== 0 || ![4, 16].includes(event[4])) return value;
                return value | (state.cardReader(event[3])?.attribute || 0);
            }, 0);
            const weight = group.reduce((value, bit) => value * (seen & bit ? 8 : [4, 16, 32].includes(bit) ? 3 : 1), 1);
            return { key: String(mask), response: msg.prepareResponse(mask), weight };
        });
    }
    if (name === 'SelectCounter') {
        function* payments(index, count, allocated) {
            if (index === msg.cards.length) {
                if (count === 0) yield allocated;
                return;
            }
            const max = Math.min(msg.cards[index].counterCount, count);
            for (let amount = 0; amount <= max; amount++)
                yield* payments(index + 1, count - amount, [...allocated, amount]);
        }
        return Array.from(payments(0, msg.counterCount, []), allocation => ({
            key: allocation.join(','),
            response: msg.prepareResponse(allocation.flatMap((count, i) =>
                count ? [{ card: P.IndexResponse(i), count }] : [])),
            weight: allocation.reduce((sum, count, i) =>
                sum + count * (msg.cards[i].code === card.savage ? 0 : 1), 1),
        }));
    }
    if (name === 'SelectSum') {
        const items = msg.cards.map((_, i) => i);
        const minimum = Math.max(0, msg.min - msg.mustSelectCards.length);
        const maximum = Math.min(items.length, msg.max ? msg.max - msg.mustSelectCards.length : items.length);
        return Array.from(selections(items, minimum, maximum)).filter(group => {
            const cards = [...msg.mustSelectCards, ...group.map(i => msg.cards[i])];
            let values = [0];
            for (const { opParam } of cards) {
                const low = opParam & 0xffff;
                const high = opParam >>> 16;
                const next = values.map(v => v + low);
                if (high) next.push(...values.map(v => v + high));
                values = next;
            }
            return msg.mode ? values.some(v => v >= msg.sumVal) : values.includes(msg.sumVal);
        }).map(group => ({
            key: group.map(i => msg.cards[i].code).join(','),
            response: msg.prepareResponse(group.map(P.IndexResponse)),
            weight: 1,
        }));
    }
    return engineOptions(msg, state, state.searchPolicy);
}

function track(msg, state, name) {
    switch (name) {
    case 'NewTurn':
        state.turn++;
        state.phase = 0;
        break;
    case 'NewPhase':
        state.phase = msg.phase;
        break;
    case 'Summoning':
    case 'SpSummoning':
        if (msg.controller === 0) state.pending++;
        break;
    case 'Summoned':
    case 'SpSummoned':
        state.summons += state.pending;
        state.pending = 0;
        break;
    case 'Chaining':
        state.chains.set(msg.chainCount, { player: msg.controller, location: msg.location, code: msg.code, desc: msg.desc });
        if (msg.controller === 0) state.chainSource = msg.code;
        if (state.turn === 2 && state.phase === 1 && msg.controller === 0 && msg.code === card.descent)
            state.drawPhaseTransforms++;
        state.events.push(['effect', state.turn, msg.controller, msg.code, msg.location, msg.desc]);
        break;
    case 'ChainSolving':
        state.solving = state.chains.get(msg.chainCount);
        if (state.solving?.player === 0) state.chainSource = state.solving.code;
        break;
    case 'ChainDisabled':
    case 'ChainNegated': {
        const interrupted = state.chains.get(msg.chainCount);
        if (interrupted) {
            state.events.push(['negated', state.turn, interrupted.player, interrupted.code]);
            if (interrupted.player === 1 && interrupted.code === trap.nibiru) state.nibiruStopped = true;
        }
        break;
    }
    case 'ChainEnd':
        state.chains.clear();
        state.solving = null;
        state.chainSource = null;
        break;
    case 'ChainSolved':
        state.solving = null;
        break;
    case 'Draw':
        if (msg.player === 1 && state.turn) {
            if (state.turn === 2 && state.phase === 1 && !state.normalDrawSeen && !state.solving) {
                state.normalDrawSeen = true;
                state.draws += Math.max(0, msg.count - 1);
            } else {
                state.draws += msg.count;
            }
        }
        if (msg.player === 0 && state.turn === 1) state.ownDraws += msg.count;
        if (state.turn) state.events.push(['draw', state.turn, msg.player, msg.count]);
        break;
    case 'Move':
        if (msg.previous.controller === 1 && msg.previous.location === 2 &&
            msg.current.location !== 2 && (msg.reason & 0x4040) === 0x4040 &&
            state.solving?.player === 0) {
            if (state.turn === 1) state.lost++;
            if (state.turn === 2 && state.phase === 1) state.extra++;
            state.events.push(['hand_rip', state.turn, state.solving.code, msg.code]);
        }
        if (msg.current.controller === 0 && msg.current.location === 4 &&
            !onField(msg.previous.location)) state.events.push(['monster', state.turn, msg.code, msg.current.sequence]);
        break;
    case 'Hint':
        if (msg.player === 0 && msg.type === 3) state.selectionHint = msg.desc;
        break;
    }
}

function refresh(duel, state) {
    state.board = query(duel, 4, 0).map(code);
    state.grave = query(duel, 16, 0, 1).map(code);
    state.hand = query(duel, 2, 0, 1).map(code);
    state.spellCount = state.grave.filter(id => state.cardReader(id)?.type & 2).length
        + query(duel, 8, 0, 1).length;
}

function rollout(core, input, policy, random, deadline, parent, pivot, cardReader, hooks) {
    const duel = openDuel(core, input);
    const state = {
        searchPolicy: policy, cardReader, board: [], grave: [], hand: input.main.slice(0, 5), spellCount: 0,
        declarations: [...new Set(input.main)], chosen: new Set(), trapActions: [], trace: [],
        events: [], turn: 0, phase: 0, chains: new Map(), summons: 0, pending: 0,
        lost: 0, extra: 0, draws: 0, ownDraws: 0, drawPhaseTransforms: 0,
        nibiruStopped: false, trapCount: input.trapCount,
    };
    const steps = [];
    const prefix = parent?.steps || [];
    const preserveOpening = new Set(input.preserveOpening);
    try {
        for (let tick = 0; tick < 850 && performance.now() < deadline; tick++) {
            const result = duel.process();
            for (const msg of result.messages || []) {
                const name = nameOf(msg);
                if (name === 'Win' || name === 'Retry') return null;
                if (name === 'Move' && msg.previous.controller === 0 && msg.previous.location === 2 &&
                    msg.current.location !== 2 && preserveOpening.has(msg.code)) return null;
                track(msg, state, name);
                if (state.turn === 2 && name === 'NewPhase' && state.phase !== 1) {
                    const board = query(duel, 4, 0);
                    const spells = query(duel, 8, 0, 0x0e400f);
                    const hand = query(duel, 2, 0, 1).map(code);
                    const grave = query(duel, 16, 0, 1).map(code);
                    const components = settledScore(board, state.lost, state.extra, state.draws, spells);
                    const progress = resolutionProgress(state, board, grave);
                    return { ...components, lost: state.lost, extra: state.extra, draws: state.draws,
                        own_draws: state.ownDraws, nibiru_stopped: state.nibiruStopped,
                        draw_phase_transforms: state.drawPhaseTransforms,
                        board: board.map(code), hand, grave, spells: spells.map(code),
                        progress, summons: state.summons, traps: state.trapActions, events: state.events, steps };
                }
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                if (msg.responsePlayer() === 1) {
                    const answer = (hooks?.opponent || opponentChoice)(msg, state, optionsFor);
                    if (!answer) return null;
                    duel.setResponse(answer);
                    continue;
                }
                if (name === 'SelectIdleCmd') refresh(duel, state);
                let choices = optionsFor(msg, state);
                if (!choices.length) return null;
                if (name === 'SelectIdleCmd') {
                    const advancing = choices.filter(c => c.weight > 1 && c.key !== 'end' && c.key !== 'battle');
                    const stopUnderG = state.draws && random() < (state.draws > 2 ? 0.20 : 0.075);
                    for (const choice of choices) {
                        if (choice.key === 'end') choice.weight = stopUnderG ? 60 : advancing.length ? 0.65 : 16;
                    }
                }
                if (name === 'SelectOption' && state.lastIdle === card.power) {
                    for (const choice of choices) if (Number(choice.key) === card.power * 16 + 2) choice.weight = 12;
                }
                if (name === 'SelectChain') {
                    const prior = [...state.chains.values()].at(-1);
                    const optional = choices.some(choice => choice.key === 'pass');
                    if (optional && prior?.player !== 1)
                        choices = choices.filter(choice => !ownDefensiveEffects.has(Number(choice.key.split(':')[0])));
                    for (const choice of choices) {
                        const id = Number(choice.key.split(':')[0]);
                        if (drawTraps.has(id)) choice.weight = 0.05;
                        if (id === card.dragon && !negateRelevant(prior, state)) choice.weight = 0.1;
                        if (id === card.savage && prior?.player === 1) choice.weight = 55;
                        if (id === card.tears && prior?.player === 1) choice.weight = 40;
                        if (id === card.descent && state.turn === 2 && state.phase === 1)
                            choice.weight = 45;
                    }
                }
                const previous = prefix[steps.length];
                let chosen;
                if (steps.length < pivot) {
                    chosen = choices.find(c => keyOf(c.response) === previous.response);
                    if (!chosen) return null;
                } else if (steps.length === pivot) {
                    const alternatives = choices.filter(c => c.key !== previous.key);
                    chosen = pick(alternatives.length ? alternatives : choices, random);
                } else if (previous?.name === name && random() < 0.35) {
                    chosen = choices.find(c => c.key === previous.key);
                }
                if (!chosen && hooks?.choose) chosen = hooks.choose({ name, state, choices, duel, step: steps.length });
                chosen ||= pick(choices, random);
                hooks?.observe?.({ name, state, choices, chosen, duel });
                const occurrence = hooks?.recordOccurrence ?
                    choices.slice(0, choices.indexOf(chosen)).filter(option => option.key === chosen.key).length : undefined;
                steps.push({ name, response: keyOf(chosen.response), key: chosen.key, occurrence,
                    branch: new Set(choices.map(c => c.key)).size > 1 });
                if (name === 'SelectIdleCmd') {
                    state.lastIdle = chosen.card;
                    state.selectionHint = undefined;
                }
                duel.setResponse(chosen.response);
            }
        }
        return null;
    } finally {
        duel.endDuel();
    }

    function negateRelevant(prior, state) {
        return prior?.player === 1 && (state.turn === 1 || prior.code === trap.nibiru);
    }
}

function resolutionProgress(state, board, grave) {
    const monsters = new Set(state.events.filter(e => e[0] === 'monster').map(e => e[2]));
    const live = new Set(board.map(code));
    return [card.wayne, card.corozo, card.four, card.selene, card.savage, card.dragon, card.moon]
        .reduce((sum, id) => sum + (monsters.has(id) ? 2 : 0) + (live.has(id) ? 1 : 0), 0)
        + (monsters.has(card.gottoms) ? 10 : 0) + state.lost * 8
        + Math.min(8, state.summons) + Math.min(6, grave.length);
}

function compare(a, b) {
    return b.score - a.score || a.draws - b.draws || b.lost - a.lost ||
        b.own_draws - a.own_draws || b.hand.length - a.hand.length || a.summons - b.summons;
}

function search(core, cardReader, input, budget, maxAttempts) {
    const turnSeed = input.searchSeed ?? input.draw_seed;
    let seed = turnSeed >>> 0;
    const random = () => {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return seed / 0x100000000;
    };
    const start = performance.now();
    const deadline = start + budget;
    const boardStates = new Map(), unsupported = {};
    let pool = [], attempts = 0, settled = 0, best;
    let bestWithDraw, bestAfterNibiru, bestAfterNibiruWithDraw;
    while (attempts < maxAttempts && performance.now() < deadline) {
        const parent = attempts % 5 && pool[Math.floor(random() ** 2 * pool.length)];
        const pivot = parent && parent.pivots[Math.floor(random() * parent.pivots.length)];
        try {
            const policy = ownPolicy(attempts++, input.earlySavage);
            const run = rollout(core, input, policy, random, deadline, parent, parent ? pivot : -1, cardReader);
            if (!run) continue;
            settled++;
            if (!best || compare(run, best) < 0) best = run;
            if (run.own_draws && (!bestWithDraw || compare(run, bestWithDraw) < 0)) bestWithDraw = run;
            if (run.nibiru_stopped && (!bestAfterNibiru || compare(run, bestAfterNibiru) < 0)) bestAfterNibiru = run;
            if (run.nibiru_stopped && run.own_draws &&
                (!bestAfterNibiruWithDraw || compare(run, bestAfterNibiruWithDraw) < 0)) bestAfterNibiruWithDraw = run;
            const key = JSON.stringify([run.score, run.lost, run.draws, [...run.board].sort(), [...run.hand].sort(), [...run.grave].sort()]);
            if (boardStates.has(key)) continue;
            run.pivots = run.steps.flatMap((step, index) => step.branch ? [index] : []);
            if (!run.pivots.length) continue;
            boardStates.set(key, run);
            pool = [...pool, run].sort((a, b) => {
                const merit = candidate => candidate.score * 2 + candidate.progress;
                return merit(b) - merit(a) || compare(a, b);
            }).slice(0, 32);
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupported[error.prompt] = (unsupported[error.prompt] || 0) + 1;
        }
    }
    const evidence = run => {
        if (!run) return null;
        const { steps, pivots, ...summary } = run;
        return summary;
    };
    return { best: evidence(best), attempts, settled, unsupported, ms: Math.round(performance.now() - start),
        ...(input.witnesses && {
            best_with_draw: evidence(bestWithDraw),
            best_after_nibiru: evidence(bestAfterNibiru),
            best_after_nibiru_with_draw: evidence(bestAfterNibiruWithDraw),
        }) };
}

async function loadEngine(fixture, data) {
    const { CardLoadWorker } = require(path.join(fixture, 'dist/src/ygopro/card-load-worker'));
    const { cardStorage, failedFiles } = await new CardLoadWorker(data).load();
    if (failedFiles.length) throw new Error('Unable to load ' + failedFiles.join(', '));
    const wasmBinary = cardStorage.ocgcoreWasmBinary;
    const core = await coreApi.createOcgcoreWrapper(wasmBinary ? { wasmBinary } : undefined);
    const cardReader = cardStorage.toCardReader();
    const scriptReader = await coreApi.DirScriptReaderEx(...[...data].reverse());
    core.setCardReader(cardReader);
    core.setScriptReader(scriptReader);
    const engineErrors = new Set();
    core.setMessageHandler((_, message, type) => {
        if (type === coreApi.OcgcoreMessageType.ScriptError) engineErrors.add(message);
    });
    return { core, cardReader, scriptReader, engineErrors };
}

async function main() {
    const [fixture, ...data] = process.argv.slice(2).map(file => path.resolve(file));
    const { core, cardReader, scriptReader, engineErrors } = await loadEngine(fixture, data);
    const inputStream = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
    const checked = new Set();
    for await (const line of inputStream) {
        if (!line.trim()) continue;
        const { input, id, budget = 2000, attempts = Infinity, allowMissingScripts = [] } = JSON.parse(line);
        for (const code of new Set([...input.main, ...input.extra, ...input.opponent.main, ...input.opponent.extra])) {
            if (checked.has(code)) continue;
            const props = cardReader(code);
            if (!props) throw new Error('Missing card data: ' + code);
            if (props.type !== 17 && !allowMissingScripts.includes(code) &&
                !await scriptReader('c' + code + '.lua') && !await scriptReader('script/c' + code + '.lua'))
                throw new Error('Missing card script: ' + code);
            if (!allowMissingScripts.includes(code)) checked.add(code);
        }
        engineErrors.clear();
        const outcome = search(core, cardReader, input, budget, attempts);
        if (engineErrors.size) throw new Error([...engineErrors].slice(0, 8).join('\n'));
        console.log(JSON.stringify({ id, ...outcome }));
    }
}

module.exports = { loadEngine, rollout, compare, optionsFor };

if (require.main === module) {
    main().catch(error => {
        console.error(error.stack);
        process.exitCode = 1;
    });
}
