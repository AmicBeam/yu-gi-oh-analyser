'use strict';

const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');
const P = require('ygopro-msg-encode');
const coreApi = require('koishipro-core.js');

const traps = { nibiru: 27204311, ash: 14558128, dragon: 70088809, droll: 94145022, ogre: 59438930, imperm: 10045474 };
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
const handTraps = new Set([23434538, 14558128, 59438930, 27204311, 94145021, 94145022, 10045474]);
const canonical = new Map([[9205574, Twin.redLink], [36609519, Twin.blueLink]]);
const norm = code => canonical.get(code) || code;

const nameOf = msg => msg.constructor.name.slice('YGOProMsg'.length);
const encoded = response => Buffer.from(response).toString('base64');
const countedDraws = new Set([9205573, 9205574, 54257392, 65330383]);

class UnsupportedPrompt extends Error {
    constructor(prompt) {
        super('Unsupported engine prompt ' + prompt);
        this.prompt = prompt;
    }
}

function* selections(items, min, max, start = 0, picked = []) {
    if (picked.length >= min)
        yield picked;
    if (picked.length >= max)
        return;
    for (let i = start; i < items.length; i++)
        yield* selections(items, min, max, i + 1, [...picked, items[i]]);
}

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

function engineOptions(msg, state) {
    const index = i => P.IndexResponse(i);
    const name = nameOf(msg);
    const option = (key, response, weight = 1) => ({ key, response, weight });
    const cardKey = c => [c.code, c.controller, c.location].join(':');
    switch (name) {
        case 'SelectIdleCmd': {
            const I = P.IdleCmdType;
            const actions = [
                [I.SUMMON, msg.summonableCards], [I.SPSUMMON, msg.spSummonableCards],
                [I.ACTIVATE, msg.activatableCards], [I.SSET, msg.ssetableCards],
                [I.MSET, msg.msetableCards], [I.REPOS, msg.reposableCards],
            ];
            const result = actions.flatMap(([type, cards]) => cards.map((c, i) => ({
                ...option([type, cardKey(c), c.desc || 0].join('/'), msg.prepareResponse(type, index(i)),
                    idleWeight(type, c, state)),
                card: norm(c.code),
            })));
            if (msg.canEp) result.push(option('end', msg.prepareResponse(I.TO_EP)));
            if (msg.canBp) result.push(option('battle', msg.prepareResponse(I.TO_BP)));
            return result;
        }
        case 'SelectChain': {
            const forced = msg.chains.some(c => c.forced);
            return [...msg.chains.flatMap((c, i) => !forced || c.forced
                ? [option([c.code, c.desc].join(':'), msg.prepareResponse(index(i)), handTraps.has(c.code) && !state.chains.size ? 1 : 16)] : []),
            ...(!forced ? [option('pass', msg.prepareResponse(), msg.chains.some(c => !handTraps.has(c.code)) ? 1 : 10)] : [])];
        }
        case 'SelectEffectYn':
        case 'SelectYesNo':
            return [true, false].map(yes =>
                option([msg.code || 0, msg.desc || 0, yes].join(':'), msg.prepareResponse(yes), yes ? 8 : 1));
        case 'SelectUnselectCard':
            return [...msg.selectableCards.map((c, i) => option(cardKey(c), msg.prepareResponse(index(i)), choicePriority(c, state))),
                ...(msg.finishable || msg.cancelable
                    ? [option('done', msg.prepareResponse(null), msg.finishable ? 12 : 1)] : [])];
        case 'SelectCard':
        case 'SelectTribute': {
            const tribute = name === 'SelectTribute';
            const indexes = msg.cards.map((_, i) => i);
            const sets = selections(indexes, tribute ? 1 : msg.min, msg.max);
            const legal = Array.from(sets).filter(group =>
                !tribute || group.reduce((sum, i) => sum + msg.cards[i].releaseParam, 0) >= msg.min);
            return [...legal.map(group => {
                const cards = group.map(i => msg.cards[i]);
                const weight = cards.length ? cards.reduce((sum, c) => sum + choicePriority(c, state), 0) : 1;
                return option(cards.map(cardKey).sort().join(','), msg.prepareResponse(group.map(index)), weight);
            }), ...(msg.cancelable ? [option('cancel', msg.prepareResponse(null))] : [])];
        }
        case 'SelectPlace':
        case 'SelectDisField':
            return Array.from(selections(msg.getSelectablePlaces(), msg.count || 1, msg.count || 1), places =>
                option(places.map(p => [p.player, p.location, p.sequence].join(':')).join(','),
                    msg.prepareResponse(places), places.some(p => p.location === 4 && [1, 3, 5, 6].includes(p.sequence)) ? 3 : 1));
        case 'SelectPosition':
            return [1, 2, 4, 8].filter(bit => msg.positions & bit)
                .map(bit => option(String(bit), msg.prepareResponse(bit), bit === 1 ? 3 : 1));
        case 'SelectOption':
            return msg.options.map((desc, i) => option(String(desc), msg.prepareResponse(index(i))));
        case 'AnnounceCard':
            // Crossout needs a copy in the deck. The engine checks each declaration.
            return state.declarations.map(code => option(String(code), msg.prepareResponse(code),
                state.trace.findLast(e => e[0] === 'Chaining' && e[2] === 1)?.[1] === code ? 12 : 1));
        case 'AnnounceNumber':
            return msg.numbers.map((number, i) => option(String(number), msg.prepareResponse(index(i))));
        case 'SelectBattleCmd':
            return [...(msg.canEp ? [option('end', msg.prepareResponse(P.BattleCmdType.TO_EP))] : []),
                ...(msg.canM2 ? [option('main2', msg.prepareResponse(P.BattleCmdType.TO_M2))] : [])];
        default:
            throw new UnsupportedPrompt(name);
    }
}

function opposingResponse(msg, state, scenario) {
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
            }[scenario];
            const choice = !state.interference && window && msg.chains.find(c => c.code === traps[scenario]);
            if (choice) {
                state.interference = true;
                state.target = ['ash', 'dragon', 'ogre', 'imperm'].includes(scenario) ? code : 0;
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

function record(msg, state) {
    const name = nameOf(msg);
    if (name === 'NewTurn') state.turn++;
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
    } else if (['ChainSolving', 'ChainSolved', 'ChainDisabled', 'ChainNegated', 'ChainEnd'].includes(name)) {
        if (name === 'ChainSolving') state.solving = state.chains.get(msg.chainCount);
        if (name === 'ChainDisabled' || name === 'ChainNegated') state.chains.delete(msg.chainCount);
        if (name === 'ChainSolved') {
            const resolved = state.chains.get(msg.chainCount);
            if (resolved?.player === 1 && [94145021, 94145022].includes(resolved.card))
                state.searchLocked = true;
            state.solving = undefined;
        }
        if (name === 'ChainEnd') state.chains.clear();
        state.trace.push([name, msg.chainCount]);
    }
    else if (name === 'Move') {
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
            msg.previous.controller, msg.previous.sequence, msg.current.controller]);
    }
    else if (name === 'Draw' || name === 'NewTurn') {
        if (name === 'Draw' && msg.player === 0 && state.solving?.player === 0
            && countedDraws.has(state.solving.card))
            state.scoringDraws++;
        state.trace.push([name, null, msg.player]);
    }
}

function openDuel(core, input) {
    const duel = core.createDuelV2([input.draw_seed >>> 0, 0, 0, 0, 0, 0, 0, 0]);
    duel.loadRegistry({ duel_mode: 'single', start_lp: '8000', start_hand: '5', draw_count: '1' });
    for (const script of ['./script/patches/entry.lua', './script/special.lua', './script/init.lua'])
        duel.preloadScript(script);
    [input, input.opponent].forEach((deck, player) => {
        duel.setPlayerInfo({ player, lp: 8000, startHand: 5, drawCount: 1 });
        for (const [cards, location] of [[deck.main, 1], [deck.extra, 64]]) {
            for (const code of [...cards].reverse())
                duel.newCard({ code, owner: player, player, location, sequence: 0, position: 8 });
        }
    });
    duel.startDuel((5 << 16) | coreApi.OcgcoreDuelOptionFlag.PseudoShuffle);
    return duel;
}

const queryFlags = 0x80000 | 0x4000 | 0xf;
const cardCode = card => card?.alias || card?.code || 0;
function zone(duel, location, player = 0) {
    return duel.queryFieldCard({ player, location, queryFlag: queryFlags, useCache: 0 }).cards;
}

function position(duel, state) {
    const monsterZone = zone(duel, 4);
    const spells = zone(duel, 8);
    const grave = zone(duel, 16);
    const codes = cards => cards.map(cardCode).filter(Boolean);
    const board = Array.from({ length: 7 }, (_, i) => cardCode(monsterZone[i]));
    const set = spells.filter(c => c.position & 10).map(cardCode);
    const equip = spells.filter(c => c.equipCard?.location === 4 && c.equipCard.controller === 0
        && board[c.equipCard.sequence] === 82135803).map(cardCode);
    const draws = state.trace.filter(e => e[0] === 'Draw' && e[2] === 0).length - 1;
    const activations = state.trace.filter(e => e[0] === 'Chaining' && e[2] === 0)
        .map(([, code, , location, desc]) => [code, location, desc]);
    const blocked = zone(duel, 4, 1);
    return {
        board, set, draws, scoring_draws: state.scoringDraws, hand: codes(zone(duel, 2)), grave: codes(grave), extra: codes(zone(duel, 64)),
        deck: codes(zone(duel, 1)), equip,
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

function rollout(core, input, scenario, prefix, random, deadline, continuation = [], mutate = false) {
    const duel = openDuel(core, input);
    const state = { trace: [], turn: 0, summons: 0, pendingSummons: 0, normal: new Set(),
        declarations: [...new Set(input.main)], chains: new Map(), scoringDraws: 0,
        field: new Map(), grave: [] };
    const decisions = [];
    const steps = [];
    let resume = 0;
    try {
        for (let tick = 0; tick < 1600 && performance.now() < deadline; tick++) {
            const result = duel.process();
            for (const msg of result.messages || []) {
                const name = nameOf(msg);
                if (name === 'Retry' || name === 'Win') return null;
                record(msg, state);
                if (!state.nibiruWindow && state.summons >= 5)
                    state.nibiruWindow = position(duel, state);
                if (state.turn === 2) {
                    if (decisions.length < prefix.length) throw new Error('Search path ended before the current branch point');
                    return { position: position(duel, state), nibiru_window: state.nibiruWindow,
                        responses: decisions, steps, trace: state.trace };
                }
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                if (msg.responsePlayer() === 1) {
                    duel.setResponse(opposingResponse(msg, state, scenario));
                    continue;
                }
                const choices = engineOptions(msg, state);
                if (!choices.length) return null;
                const previous = prefix[decisions.length];
                let selected;
                if (previous !== undefined) {
                    selected = choices.find(choice => encoded(choice.response) === previous);
                    if (!selected) throw new Error('Search path diverged at prompt ' + decisions.length);
                } else if (mutate && decisions.length === prefix.length && continuation[0]?.name === name) {
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
                if (name === 'SelectIdleCmd') state.lastIdle = selected.card;
                if (choices.length > 1) {
                    steps.push({ name, key: selected.key, offset: decisions.length });
                }
                if (['SelectCard', 'SelectTribute', 'SelectUnselectCard'].includes(name) && !msg.selectableCards)
                    state.selectionHint = undefined;
                decisions.push(encoded(selected.response));
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
    const { input, scenario, budget, scorer, defaultWasm } = JSON.parse(fs.readFileSync(inputFile));
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
    const unsupportedPrompts = {};
    function scoreBatch() {
        const remaining = Math.floor(deadline - performance.now());
        if (!scorer || !pending.length || remaining < 70) return;
        const observations = pending.map(({ position, nibiru_window }) => JSON.stringify({ position, nibiru_window }));
        const result = spawnSync('mono', [scorer, fixture + '/cards.cdb'], {
            input: observations.join('\n') + '\n', encoding: 'utf8', timeout: remaining,
        });
        if (result.error?.code === 'ETIMEDOUT') return;
        if (result.status !== 0) throw new Error(result.stderr || String(result.error));
        const points = result.stdout.trim().split('\n').map(Number);
        if (points.length !== pending.length || points.some(point => !Number.isInteger(point)))
            throw new Error('Invalid terminal scorer output');
        pending.forEach((run, i) => { run.points = points[i]; });
        pending = [];
        pool = [...finalists.values()].sort(compare).slice(0, 48);
    }

    while (performance.now() < deadline) {
        const exploreFromStart = attempts++ % 5 === 0;
        const possibleParent = exploreFromStart ? null : pool[Math.floor(random() ** 2 * pool.length)];
        const parent = possibleParent?.steps.length ? possibleParent : null;
        const fork = parent && Math.floor(random() * parent.steps.length);
        const prefix = parent ? parent.responses.slice(0, parent.steps[fork].offset) : [];
        const guide = parent ? parent.steps.slice(fork) : [];
        let run;
        try {
            run = rollout(core, input, scenario, prefix, random, deadline, guide, Boolean(parent));
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupportedPrompts[error.prompt] = (unsupportedPrompts[error.prompt] || 0) + 1;
            continue;
        }
        if (!run || (scenario && !run.trace.some(e => e[0] === 'Interference'))) continue;
        const p = run.position;
        const key = JSON.stringify([p.board, p.set, p.draws, p.hand, p.grave, p.extra, p.equip,
            p.normal_zones, p.unrevivable, p.scoring_draws, run.nibiru_window]);
        if (finalists.has(key)) continue;
        run.rank = rank(p);
        finalists.set(key, run);
        pending.push(run);
        pool = [...pool, run].sort(compare).slice(0, 48);
        if (pending.length >= 16) scoreBatch();
    }
    const ranked = [...finalists.values()].sort(compare).slice(0, 160);
    const sorted = [...new Set([...ranked, ...pending])];
    for (const run of sorted) {
        delete run.steps;
        delete run.responses;
    }
    console.log(JSON.stringify({ attempts, reference_search_ms: Math.round(performance.now() - start), unsupported_prompts: unsupportedPrompts, candidates: sorted }));

}

main().catch(error => {
    console.error(error.stack);
    process.exitCode = 1;
});
