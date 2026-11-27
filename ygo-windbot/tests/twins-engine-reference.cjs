'use strict';

const fs = require('node:fs');
const { createHash } = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');
const P = require('ygopro-msg-encode');
const { belle, sunnyRevival } = require('./twins-interference.cjs');
const { MixedInterference } = require('./twins-mixed-interference.cjs');
const coreApi = require('koishipro-core.js');
const { engineOptions: protocolOptions, nameOf, openDuel, UnsupportedPrompt } =
    require('./engine-search-protocol.cjs');
const encoded = response => Buffer.from(response).toString('base64');

const traps = { nibiru: 27204311, ash: 14558128, dragon: 70088809, droll: 94145022, ogre: 59438930, imperm: 10045474, belle };
const master = 72444406;
const linkTwos = [9205573, 9205574, 36609518, 36609519, 25153, 49867899, 24269961];
const extraPayoff = new Map([[82135803, 20], [65330383, 20], [4731783, 12], [25154, 12], [79559912, 13], [93672138, 12]]);
const twinTraps = [98360333, 25156];
const comboSpells = new Set([37582948, 61976639, 25151]);
const Twin = {
    red: 36326160, blue: 73810864, frost: 54257392, sweet: 82699999, newYear: 25150, photo: 25151,
    scout: 25152, heist: 25153, nightfall: 25154, trouble: 37582948, password: 61976639,
    deal: 6636319, sunny: 93672138, redLink: 9205573, blueLink: 36609518,
};
const Fiendsmith = { requiem: 2463794, engraver: 60764609, sequence: 49867899, desirae: 82135803 };
const Abao = 4731783, Yama = 24269961, Gryphon = 65330383;
const handTraps = new Set([23434538, 14558128, 59438930, 27204311, 94145021, 94145022, 10045474, belle]);
const canonical = new Map([[9205574, Twin.redLink], [36609519, Twin.blueLink]]);
const norm = code => canonical.get(code) || code;

const countedDraws = new Set([9205573, 9205574, 54257392, 65330383]);

function idleWeight(type, card, state) {
    const I = P.IdleCmdType;
    const code = norm(card.code);
    const field = new Set(state.field.values());
    const grave = new Set(state.grave);
    const boardEstablished = field.has(Fiendsmith.desirae) || state.scoringDraws !== 0;
    switch (type) {
        case I.SUMMON:
            return [Twin.red, Twin.blue, Twin.sweet].includes(code) ? 60 : 12;
        case I.SPSUMMON:
            if ([Twin.scout, Twin.deal, Fiendsmith.requiem].includes(code)) return 48;
            if (code === Yama) return field.has(Fiendsmith.engraver) ? 54 : 16;
            if (code === Abao) return field.has(Yama) ? 64 : 21;
            if (code === Fiendsmith.sequence) return field.has(Fiendsmith.engraver) ? 42 : 15;
            if (code === Twin.sunny) return field.has(Twin.redLink) && field.has(Twin.blueLink) ? 37 : 10;
            if (code === Twin.nightfall) return boardEstablished ? 49 : 8;
            if (code === Gryphon) return boardEstablished ? 36 : 9;
            return linkTwos.includes(code) ? 22 : 12;
        case I.ACTIVATE:
            if (handTraps.has(code)) return 1;
            if (code === Twin.photo) return boardEstablished || grave.has(Yama) ? 22 : 8;
            if (code === Twin.trouble || code === Twin.password) return state.summons >= 5 ? 22 : 11;
            if ([Twin.sweet, Twin.newYear, Twin.deal, Twin.scout, Fiendsmith.requiem, Abao].includes(code))
                return 54;
            if (comboSpells.has(code)) return 24;
            return 18;
        case I.SSET:
            return twinTraps.includes(code) ? 12 : 1;
        default:
            return 1;
    }
}

function choicePriority(card, state) {
    const code = norm(card.code);
    const hint = state?.selectionHint;
    const source = state?.trace.findLast(event => event[0] === 'Chaining' && event[2] === 0)?.[1];
    const heldExtender = [Twin.newYear, Twin.frost, Twin.sweet, Twin.photo, Twin.trouble, Twin.password, Fiendsmith.engraver]
        .includes(code);
    if (hint === 504 && card.location === 1) {
        if (source === Twin.scout || state.lastIdle === Twin.scout)
            return [Twin.newYear, Twin.frost].includes(code) ? 18 : 1;
        if (source === Twin.deal || state.lastIdle === Twin.deal)
            return code === Twin.sweet ? 18 : 1;
        return heldExtender ? 7 : 1;
    }
    if (hint === 506 && source === Twin.newYear)
        return code === Twin.password ? 24 : 1;
    if (hint === 506 && source === Twin.password)
        return code === Twin.trouble ? 18 : 3;
    if (hint === 506 && source === Twin.trouble)
        return code === Twin.photo ? 18 : heldExtender ? 7 : 1;
    if (hint === 509 && source === Fiendsmith.requiem)
        return code === Fiendsmith.engraver ? 24 : 1;
    if (hint === 509 && source === Abao && [Twin.red, Twin.blue].includes(code))
        return 12;
    if ([500, 501, 503, 511, 513, 533].includes(hint)) {
        if (hint === 533 && state.lastIdle === Abao && code === Yama) return 18;
        return extraPayoff.has(code) || card.location === 2 && heldExtender ? 1 : 5;
    }
    if (hint === 506 || hint === 509)
        return heldExtender || twinTraps.includes(code) ? 6 : 2;
    return 2;
}

const twinsPolicy = { idleWeight, choicePriority, normalize: norm, handTraps };

function engineOptions(msg, state) {
    return protocolOptions(msg, state, twinsPolicy);
}

function opposingResponse(msg, state, scenario) {
    if (state.mixed) {
        switch (nameOf(msg)) {
            case 'SelectChain':
                return msg.prepareResponse(msg.chains.find(c => c.forced)
                    || state.mixed.choose(msg, state.phase, state.summons, state.pendingSummons));
            case 'SelectCard':
            case 'SelectUnselectCard':
                return state.mixed.chooseCards(msg, nameOf(msg) === 'SelectUnselectCard');
            case 'SelectOption':
                return state.mixed.option(msg);
            case 'SelectEffectYn':
            case 'SelectYesNo':
                return msg.prepareResponse(true);
        }
    }
    switch (nameOf(msg)) {
        case 'SelectChain': {
            const forced = msg.chains.find(c => c.forced);
            if (forced)
                return msg.prepareResponse(forced);
            const masterEffect = scenario === 'dragon' && state.interference && msg.chains.find(c => c.code === master);
            if (masterEffect) {
                state.masterStarted = true;
                return msg.prepareResponse(masterEffect);
            }
            const prior = state.trace.findLast(e => e[0] === 'Chaining');
            const ownEffect = prior?.[2] === 0 && prior[3] === 4;
            const code = prior?.[1];
            const window = {
                nibiru: state.summons === 5, ash: ownEffect && code === 2463794,
                dragon: ownEffect && code === 6636319, droll: true,
                ogre: ownEffect && code === 4731783, imperm: ownEffect && linkTwos.includes(code),
                belle: sunnyRevival(prior),
            }[scenario];
            const choice = !state.interference && window && msg.chains.find(c => c.code === traps[scenario]);
            if (choice) {
                state.interference = true;
                state.target = ['ash', 'dragon', 'ogre', 'imperm', 'belle'].includes(scenario) ? code : 0;
                state.trace.push(['Interference', scenario, choice.code, state.target, state.summons]);
            }
            return msg.prepareResponse(choice || undefined);
        }
        case 'SelectCard': {
            const extra = msg.cards.filter(c => c.location === 64);
            if (scenario === 'dragon' && extra.length) {
                extra.sort((a, b) => Number(b.code === master) - Number(a.code === master));
                return msg.prepareResponse(extra.slice(0, msg.min === msg.max ? msg.min : Math.min(5, msg.max)));
            }
            const target = state.masterStarted
                ? msg.cards.find(c => c.controller === 0 && c.location === 4 && c.code && c.code !== state.target)
                : msg.cards.find(c => c.controller === 0 && c.location === 4 && c.code === state.target);
            if (target)
                state.trace.push([state.masterStarted ? 'MasterTarget' : 'InterferenceTarget', target.code, target.sequence]);
            return msg.prepareResponse(target ? [target] : msg.cards.slice(0, msg.min));
        }
        case 'SelectUnselectCard': {
            const choice = msg.selectableCards.find(c => c.code === master) || msg.selectableCards[0];
            const done = msg.finishable && (scenario !== 'dragon' || msg.unselectableCards.length >= 5);
            return msg.prepareResponse(done ? null : choice);
        }
        case 'SelectEffectYn':
        case 'SelectYesNo':
            if (msg.code === master) state.masterStarted = true;
            return msg.prepareResponse(scenario === 'dragon' && state.interference);
        default: {
            const [option] = engineOptions(msg, state);
            return option.response;
        }
    }
}

function record(msg, state, scenario) {
    const name = nameOf(msg);
    if (name === 'NewTurn') state.turn++;
    if (name === 'NewPhase') state.phase = msg.phase;
    if (name === 'Hint' && msg.player === 0 && msg.type === 3)
        state.selectionHint = msg.desc;
    if (name === 'Summoning' || name === 'SpSummoning') {
        if (msg.controller === 0) {
            state.pendingSummons++;
            if (name === 'Summoning') state.normal.add(msg.sequence);
            if (name === 'SpSummoning' && msg.code === 2463794 && state.summons + state.pendingSummons <= 5)
                state.earlyRequiem ||= state.trace.findLast(e => e[0] === 'Move' && e[1] === msg.code)?.[2] === 64;
        }
    }
    if (name === 'Summoned' || name === 'SpSummoned') {
        state.summons += state.pendingSummons;
        state.pendingSummons = 0;
    }
    if (name === 'Chaining') {
        state.chains.set(msg.chainCount, { card: msg.code, player: msg.controller });
        state.trace.push([name, msg.code, msg.controller, msg.location, msg.desc, msg.chainCount]);
        if (state.mixed && msg.controller === 1) {
            if (state.onRecordedPath && state.recoveryOrdinals.has(state.opponentActivations))
                state.recoveryLinks.add(msg.chainCount);
            if (state.onRecordedPath && state.hardRecoveryOrdinals.has(state.opponentActivations))
                state.hardRecoveryLinks.add(msg.chainCount);
            state.opponentActivations++;
        }
    } else if (['ChainSolving', 'ChainSolved', 'ChainDisabled', 'ChainNegated', 'ChainEnd'].includes(name)) {
        if (name === 'ChainSolving') state.solving = state.chains.get(msg.chainCount);
        if (name === 'ChainDisabled' || name === 'ChainNegated') state.chains.delete(msg.chainCount);
        if (name === 'ChainSolved') {
            const resolved = state.chains.get(msg.chainCount);
            if (resolved?.player === 1 && [94145021, 94145022].includes(resolved.card))
                state.searchLocked = true;
            if (resolved?.player === 1 && resolved.card === traps[scenario] && state.interference)
                state.hitResolved = true;
            if (state.recoveryLinks?.has(msg.chainCount) && resolved?.player === 1)
                state.recoveryPending = true;
            if (state.hardRecoveryLinks?.has(msg.chainCount) && resolved?.player === 1)
                state.hardRecoveryPending = true;
            state.solving = undefined;
        }
        if (name === 'ChainEnd') {
            state.chains.clear();
            if (state.recoveryPending)
                state.hitResolved = true;
            if (state.hardRecoveryPending)
                state.hardHitResolved = true;
            state.recoveryPending = false;
            state.hardRecoveryPending = false;
            state.recoveryLinks?.clear();
            state.hardRecoveryLinks?.clear();
        }
        state.trace.push([name, msg.chainCount]);
    }
    else if (name === 'Move') {
        if (state.mixed?.solving === 30316053 && msg.previous.controller === 0 && msg.current.location === 16)
            state.pendingSummons = 0;
        if (msg.previous.controller === 0 && msg.previous.location === 4) {
            state.normal.delete(msg.previous.sequence);
            if (state.newYearZone === msg.previous.sequence) state.newYearZone = undefined;
        }
        if (msg.code === 25150 && msg.previous.location === 16 && msg.current.location === 4 && msg.current.controller === 0)
            state.newYearZone = msg.current.sequence;
        if (msg.previous.controller === 0 && msg.previous.location === 4)
            state.field.delete(msg.previous.sequence);
        if (msg.current.controller === 0 && msg.current.location === 4)
            state.field.set(msg.current.sequence, norm(msg.code));
        if (msg.previous.controller === 0 && msg.previous.location === 16) {
            const previous = state.grave.indexOf(norm(msg.code));
            if (previous >= 0) state.grave.splice(previous, 1);
        }
        if (msg.current.controller === 0 && msg.current.location === 16)
            state.grave.push(norm(msg.code));
        state.trace.push([name, msg.code, msg.previous.location, msg.current.location, msg.current.sequence,
            msg.previous.controller, msg.previous.sequence, msg.current.controller,
            ...(state.mixed && !state.onRecordedPath ? [msg.current.position] : [])]);
    }
    else if (name === 'Draw' || name === 'NewTurn') {
        if (name === 'Draw' && msg.player === 1 && state.turn === 1)
            state.opponentDraws += msg.cards.length;
        if (name === 'Draw' && msg.player === 0 && state.solving?.player === 0
            && countedDraws.has(state.solving.card))
            state.scoringDraws++;
        state.trace.push([name, null, msg.player, ...(state.mixed && name === 'Draw' ? [msg.cards.length] : [])]);
    }
    if (state.mixed && ['Chaining', 'ChainSolving', 'ChainSolved', 'ChainEnd', 'Move'].includes(name))
        state.mixed.observe(state.trace.at(-1));
}

const queryFlags = 0x80000 | 0x4000 | 0xf;
const cardCode = card => card?.alias || card?.code || 0;
function zone(duel, location, player = 0) {
    return duel.queryFieldCard({ player, location, queryFlag: queryFlags, useCache: 0 }).cards;
}

function position(duel, state, player = 0) {
    const monsterZone = zone(duel, 4, player);
    const spells = zone(duel, 8, player);
    const grave = zone(duel, 16, player);
    const codes = cards => cards.map(cardCode).filter(Boolean);
    const board = Array.from({ length: 7 }, (_, i) => cardCode(monsterZone[i]));
    const set = spells.filter(c => c.position & 10).map(cardCode);
    const equip = spells.filter(c => c.equipCard?.location === 4 && c.equipCard.controller === player
        && board[c.equipCard.sequence] === 82135803).map(cardCode);
    const draws = state.trace.filter(e => e[0] === 'Draw' && e[2] === 0).length - 1;
    const activations = state.trace.filter(e => e[0] === 'Chaining' && e[2] === 0)
        .map(([, code, , location, desc]) => [code, location, desc]);
    const blocked = zone(duel, 4, 1 - player);
    return {
        board, set, draws, scoring_draws: state.scoringDraws, hand: codes(zone(duel, 2, player)), grave: codes(grave), extra: codes(zone(duel, 64, player)),
        deck: codes(zone(duel, 1, player)), equip,
        unrevivable: grave.filter(c => (c.type & 0x58020c0) && !(c.status & 8)).map(cardCode),
        normal_zones: board.reduce((bits, code, i) => bits | (code && state.normal.has(i) ? 1 << i : 0), 0),
        blocked_zones: (blocked[5]?.code ? 1 << 6 : 0) | (blocked[6]?.code ? 1 << 5 : 0),
        spell_slots: spells.slice(0, 5).filter(c => c.code).length,
        activations, summons: state.summons, early_requiem: Boolean(state.earlyRequiem),
        new_year_zone: state.newYearZone ?? -1,
        search_locked: Boolean(state.searchLocked),
    };
}

function weightedChoice(choices, random) {
    let threshold = random() * choices.reduce((total, choice) => total + choice.weight, 0);
    return choices.find(choice => (threshold -= choice.weight) < 0) || choices.at(-1);
}

function branchSnapshot(duel, state, afterHardHit) {
    const p = position(duel, state);
    return { board: p.board, hand: p.hand, grave: p.grave, extra: p.extra, summons: p.summons,
        draws: p.scoring_draws, set: p.set, search_locked: p.search_locked,
        ...(state.mixed && { after_hard_hit: Boolean(afterHardHit) }) };
}

function rollout(core, input, scenario, random, deadline, options = {}) {
    const { prefix = [], continuation = [], mutate = false, collect = false, coach,
        wirePrefix = [], exact = false } = options;
    const duel = openDuel(core, input, coach);
    const state = { trace: [], turn: 0, summons: 0, pendingSummons: 0, normal: new Set(),
        declarations: [...new Set(input.main)], chains: new Map(), scoringDraws: 0,
        field: new Map(), grave: [], opponentDraws: 0, opponentActivations: 0 };
    if (coach?.mixed) {
        state.mixed = new MixedInterference(state.trace, state.field, true);
        state.recoveryOrdinals = new Set(coach.recoveryOrdinals);
        state.hardRecoveryOrdinals = new Set(coach.hardRecoveryOrdinals);
        state.recoveryLinks = new Set();
        state.hardRecoveryLinks = new Set();
        state.onRecordedPath = true;
    }
    if (!coach && input.mixed_search && input.opening_traps.length)
        state.mixed = new MixedInterference(state.trace, state.field, true);
    const decisions = [];
    const wire = [];
    const steps = [];
    const branches = [];
    const pivots = [];
    function appendWire(response) {
        if (state.onRecordedPath && response !== coach.responses[wire.length])
            state.onRecordedPath = false;
        wire.push(response);
    }
    let branch;
    let resume = 0;
    try {
        for (let tick = 0; tick < 1600 && performance.now() < deadline; tick++) {
            const result = duel.process();
            for (const msg of result.messages || []) {
                const name = nameOf(msg);
                if (name === 'Retry' || name === 'Win') return null;
                record(msg, state, scenario);
                if (!state.nibiruWindow && state.summons >= 5)
                    state.nibiruWindow = position(duel, state);
                if (state.turn === 2) {
                    if (decisions.length < prefix.length || (!exact && wire.length < wirePrefix.length))
                        throw new Error('Search path ended before the current branch point');
                    if (branch?.stop)
                        branches.push([branch.prefix, branch.action, branch.stop]);
                    return { position: position(duel, state), nibiru_window: state.nibiruWindow,
                        responses: decisions, steps, pivots, trace: state.trace,
                        ...(collect && { branches, opponentDraws: state.opponentDraws }),
                        ...(coach && { wire, hitResolved: state.hitResolved }) };
                }
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                const raw = wirePrefix[wire.length];
                if (exact && raw === undefined) return null;
                if (msg.responsePlayer() === 1) {
                    const response = opposingResponse(msg, state, scenario);
                    if (coach) appendWire(raw ?? encoded(response));
                    duel.setResponse(raw === undefined ? response : Buffer.from(raw, 'base64'));
                    continue;
                }
                let choices;
                try {
                    choices = engineOptions(msg, state);
                } catch (error) {
                    if (raw === undefined || !(error instanceof UnsupportedPrompt)) throw error;
                    choices = [];
                }
                if (!choices.length && raw === undefined) return null;
                if (collect && name === 'SelectIdleCmd' && branch) {
                    branches.push([branch.prefix, branch.action, branchSnapshot(duel, state, branch.afterHardHit)]);
                    branch = undefined;
                }
                const previous = prefix[decisions.length];
                let selected;
                if (raw !== undefined) {
                    selected = choices.find(choice => encoded(choice.response) === raw)
                        || { response: Buffer.from(raw, 'base64') };
                } else if (previous !== undefined) {
                    selected = choices.find(choice => encoded(choice.response) === previous);
                    if (!selected) throw new Error('Search path diverged at prompt ' + decisions.length);
                } else if (mutate && (coach ? wire.length === wirePrefix.length : decisions.length === prefix.length)
                    && continuation[0]?.name === name) {
                    const variants = choices.filter(choice => choice.key !== continuation[0].key);
                    selected = weightedChoice(variants.length ? variants : choices, random);
                    resume = 1;
                } else if (choices.length > 1) {
                    const lookahead = continuation.slice(resume, resume + 10);
                    const match = lookahead.findIndex(step => step.name === name && choices.some(c => c.key === step.key));
                    if (match >= 0) {
                        selected = choices.find(c => c.key === lookahead[match].key);
                        resume += match + 1;
                    }
                }
                selected ||= weightedChoice(choices, random);
                const recoveryWindow = !scenario && !coach?.mixed || state.hitResolved;
                if (collect && name === 'SelectIdleCmd' && choices.length > 1
                    && (!coach || recoveryWindow)) {
                    branch = {
                        prefix: createHash('sha256').update((coach ? wire : decisions).join('|')).digest('hex').slice(0, 24),
                        action: encoded(selected.response),
                        afterHardHit: state.hardHitResolved,
                        stop: (coach?.mixed || input.mixed_search) && selected.key === 'end'
                            ? branchSnapshot(duel, state, state.hardHitResolved) : null,
                    };
                }
                if (name === 'SelectIdleCmd') state.lastIdle = selected.card;
                if (choices.length > 1 && selected.key !== undefined) {
                    if (!coach || (name === 'SelectIdleCmd' && recoveryWindow))
                        pivots.push(steps.length);
                    steps.push({ name, key: selected.key, offset: decisions.length, wireOffset: wire.length });
                }
                if (['SelectCard', 'SelectTribute', 'SelectUnselectCard'].includes(name) && !msg.selectableCards)
                    state.selectionHint = undefined;
                decisions.push(encoded(selected.response));
                if (coach) appendWire(decisions.at(-1));
                duel.setResponse(selected.response);
            }
        }
        return null;
    } finally {
        duel.endDuel();
    }
}

function rank(p) {
    return p.board.reduce((sum, id) => sum + (extraPayoff.get(id) || (id ? 3 : 0)), 0)
        + p.set.filter(id => twinTraps.includes(id)).length * 9 + p.draws * 4;
}

async function main() {
    const [fixture, inputFile] = process.argv.slice(2);
    const { input, scenario, budget, scorer, defaultWasm, trainingAttempts, coach } = JSON.parse(fs.readFileSync(inputFile));
    const { CardLoadWorker } = require(fixture + '/dist/src/ygopro/card-load-worker');
    const paths = [fixture + '/ygopro'];
    const { cardStorage, failedFiles } = await new CardLoadWorker(paths, undefined, defaultWasm).load();
    if (failedFiles.length) throw new Error('Could not load engine card data: ' + failedFiles.join(', '));
    const wasmBinary = cardStorage.ocgcoreWasmBinary;
    const core = await coreApi.createOcgcoreWrapper(wasmBinary ? { wasmBinary } : undefined);
    core.setCardReader(cardStorage.toCardReader());
    core.setScriptReader(await coreApi.DirScriptReaderEx(...paths));
    let seed = input.draw_seed >>> 0;
    function random() {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        return seed / 0x100000000;
    }
    const start = performance.now();
    const deadline = start + budget;
    const finalists = new Map();
    const compare = (a, b) => (b.points ?? -Infinity) - (a.points ?? -Infinity) || b.rank - a.rank;
    let pool = [];
    let attempts = 0;
    let pending = [];
    const branchRuns = [];
    const unsupportedPrompts = {};
    const coverageTraces = new Map();
    const directMixed = Boolean(input.mixed_search && !coach);
    const withOpponentDraws = directMixed || coach?.mixed;
    function scoreBatch() {
        const remaining = Math.floor(deadline - performance.now());
        if (!scorer || !pending.length || remaining < 70) return;
        const observations = pending.map(({ position, nibiru_window }) => JSON.stringify({ position, nibiru_window }));
        const [command, ...args] = Array.isArray(scorer) ? scorer : ['mono', scorer];
        const result = spawnSync(command, [...args, fixture + '/cards.cdb'], {
            input: observations.join('\n') + '\n', encoding: 'utf8', timeout: remaining,
        });
        if (result.error?.code === 'ETIMEDOUT') return;
        if (result.status !== 0) throw new Error(result.stderr || String(result.error));
        const points = result.stdout.trim().split('\n').map(Number);
        if (points.length !== pending.length || points.some(point => !Number.isFinite(point)))
            throw new Error('Invalid terminal scorer output');
        pending.forEach((run, i) => {
            run.points = points[i] - (directMixed ? run.opponentDraws * 10 : 0);
        });
        pending = [];
        pool = [...finalists.values()].sort(compare).slice(0, 48);
    }

    let demonstration;
    const qualified = run => {
        if (!run || !scenario) return Boolean(run);
        const marker = run.trace.findIndex(e => e[0] === 'Interference');
        if (marker < 0) return false;
        if (!coach) return true;
        if (!run.hitResolved) return false;
        if (scenario === 'nibiru')
            return run.trace.slice(marker).some(e => e[0] === 'Move' && e[1] === traps.nibiru && e[3] === 4);
        if (scenario === 'belle') {
            const sunny = run.trace.slice(0, marker).findLast(e => e[0] === 'Chaining');
            if (!sunnyRevival(sunny)) return false;
            const end = run.trace.findIndex((e, i) => i > marker && e[0] === 'ChainEnd');
            return end > marker && run.trace.slice(marker, end).some(e =>
                ['ChainNegated', 'ChainDisabled'].includes(e[0]) && e[1] === sunny[5]);
        }
        return true;
    };
    while (performance.now() < deadline && (!trainingAttempts || attempts < trainingAttempts)) {
        const exact = Boolean(coach && attempts === 0);
        if (coach?.mixed && !exact && !demonstration?.pivots.length) break;
        const exploreFromStart = attempts++ % 5 === 0;
        let candidate = null;
        if (!exact && (!exploreFromStart || coach?.mixed)) {
            const useDemonstration = demonstration?.pivots.length && attempts % 2 === 0;
            candidate = useDemonstration ? demonstration
                : pool[Math.floor(random() ** 2 * pool.length)] || (coach?.mixed ? demonstration : null);
        }
        const parent = candidate?.pivots.length ? candidate : null;
        const pivot = parent && parent.pivots[Math.floor(random() * parent.pivots.length)];
        const step = parent && parent.steps[pivot];
        let wirePrefix = [];
        if (exact)
            wirePrefix = coach.responses;
        else if (parent && coach)
            wirePrefix = parent.wire.slice(0, step.wireOffset);
        const options = {
            prefix: parent && !coach ? parent.responses.slice(0, step.offset) : [],
            continuation: parent ? parent.steps.slice(pivot) : [],
            mutate: Boolean(parent), collect: Boolean(trainingAttempts), coach, exact, wirePrefix,
        };
        let run;
        try {
            run = rollout(core, input, scenario, random, deadline, options);
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupportedPrompts[error.prompt] = (unsupportedPrompts[error.prompt] || 0) + 1;
            continue;
        }
        if (exact && qualified(run)) demonstration = run;
        if (!qualified(run)) continue;
        const p = run.position;
        if (directMixed) {
            for (const [, card, , , effect, link] of run.trace.filter(e => e[0] === 'Chaining' && e[2] === 1)) {
                const key = [card, effect, link].join('/');
                if (!coverageTraces.has(key) && coverageTraces.size < 40) coverageTraces.set(key, run.trace);
            }
        }
        const key = trainingAttempts ? JSON.stringify([p, run.nibiru_window, ...(withOpponentDraws ? [run.opponentDraws] : [])])
            : JSON.stringify([p.board, p.set, p.draws, p.hand, p.grave, p.extra, p.equip,
                p.normal_zones, p.unrevivable, p.scoring_draws, run.nibiru_window]);
        if (trainingAttempts) {
            branchRuns.push({ terminal: finalists.get(key) || run, branches: run.branches });
            delete run.branches;
        }
        if (finalists.has(key)) continue;
        run.rank = rank(p);
        finalists.set(key, run);
        pending.push(run);
        pool = [...pool, run].sort(compare).slice(0, 48);
        if (pending.length >= 16) scoreBatch();
    }
    const stats = { attempts, reference_search_ms: Math.round(performance.now() - start),
        unsupported_prompts: unsupportedPrompts };
    if (trainingAttempts) {
        const runs = [...finalists.values()];
        const terminalIndex = new Map(runs.map((run, index) => [run, index]));
        const training = {
            terminals: runs.map(({ position, nibiru_window, opponentDraws }) => ({
                position, nibiru_window, ...(withOpponentDraws && { opponent_draws: opponentDraws }),
            })),
            runs: branchRuns.map(({ terminal, branches }) => [terminalIndex.get(terminal), branches]),
        };
        const best = directMixed && runs.filter(run => run.points !== undefined).sort(compare)[0];
        const replay = best && rollout(core, input, scenario, random, performance.now() + 2500,
            { prefix: best.responses, collect: true });
        const verified = replay && replay.responses.length === best.responses.length
            && JSON.stringify(replay.position) === JSON.stringify(best.position)
            && JSON.stringify(replay.nibiru_window) === JSON.stringify(best.nibiru_window)
            && replay.opponentDraws === best.opponentDraws;
        console.log(JSON.stringify({ ...stats, training, ...(directMixed && {
            verified: Boolean(verified), witness: verified ? terminalIndex.get(best) : null,
            coverage: [...new Set(coverageTraces.values())],
        }), demonstration: demonstration && {
            terminal: terminalIndex.get(demonstration),
            pivots: demonstration.pivots.length,
            opponent_draws: demonstration.opponentDraws,
            trace: demonstration.trace,
        } }));
        return;
    }

    const ranked = [...finalists.values()].sort(compare).slice(0, 160);
    const sorted = [...new Set([...ranked, ...pending])];
    for (const run of sorted) {
        delete run.steps;
        delete run.responses;
        delete run.pivots;
    }
    console.log(JSON.stringify({ ...stats, candidates: sorted }));

}

if (require.main === module) {
    main().catch(error => {
        console.error(error.stack);
        process.exitCode = 1;
    });
}

module.exports = { twinsPolicy, position };
