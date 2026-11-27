'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { parseArgs } = require('node:util');
const P = require('ygopro-msg-encode');
const { engineOptions, openDuel, nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { loadEngine, optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { summonPosition } = require('./duel-policy.cjs');

const C = {
    chief: 50486289, fighter: 101307006, curse: 101307007, harpie: 101307008,
    princess: 84539520, call: 57312333, rota: 32807846, mirror: 101307053,
    rescue: 101307074, formation: 101307054, sword: 101307032, baronne: 84815190,
    george: 70522875, legion: 12496261, dugares: 66011101, sheep: 50277356,
    prism: 65400671, shrimp: 92744676, rose: 65302903, saryuja: 74997493, empress: 23965033, dragon: 101307050,
    ash: 14558128, imperm: 10045474, maxx: 23434539, nibiru: 27204311,
    veiler: 97268402, belle: 73642296, ogre: 59438930, droll: 94145021,
};
const starters = new Set([C.chief, C.fighter, C.curse, C.princess, C.call, C.rota]);
const bodies = new Set([C.chief, C.fighter, C.curse, C.princess, C.harpie, C.sword, C.empress]);
const extraLines = new Set([C.dugares, C.sheep, C.prism, C.george, C.baronne, C.saryuja, C.legion, C.sword]);
const handTraps = new Set([C.ash, C.imperm, C.maxx, C.nibiru, C.veiler, C.belle, C.ogre, C.droll]);
const interactionCards = new Set([C.rescue, C.formation, C.imperm]);
const preferredTuners = new Set([C.ash, C.ogre, C.belle, C.rose]);
const { HINTMSG_DISCARD, HINTMSG_LMATERIAL, HINTMSG_SPSUMMON, HINTMSG_XMATERIAL } = P.OcgcoreScriptConstants;
const policy = { includeSequence: true, handTraps, normalize: id => id,
    idleWeight: () => 1, choicePriority: () => 1 };
const hash = value => createHash('sha256').update(value).digest('hex');
const wireOf = response => Buffer.from(response).toString('base64');
const byDescending = (a, b) => b.points - a.points || b.followUp - a.followUp || a.summons - b.summons;
const fixedWeight = (condition, weight) => condition ? weight : 1;
const query = (duel, player, location, queryFlag = 1) => duel
    .queryFieldCard({ player, location, queryFlag, useCache: 0 }).cards.filter(c => c.code);

function rng(seed) {
    return () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) / 2 ** 32);
}

function shuffle(cards, random) {
    const result = [...cards];
    for (let i = result.length - 1; i > 0; i--) {
        const j = Math.floor(random() * (i + 1));
        [result[i], result[j]] = [result[j], result[i]];
    }
    return result;
}

function readDeck(text) {
    const main = [], extra = [];
    let target;
    for (const line of text.split(/\r?\n/)) {
        if (line === '#main') target = main;
        if (line === '#extra') target = extra;
        if (line === '!side') break;
        if (/^\d+$/.test(line) && target) target.push(Number(line));
    }
    if (main.length < 40 || extra.length !== 15) throw new Error('Expected a legal Amazoness deck');
    return { main, extra };
}

function buildCases(deck, count, holdout, seed, focus) {
    const random = rng(seed), seen = new Set(), result = [];
    for (let index = 0; index < count + holdout; index++) {
        const pressure = [0, C.ash, C.imperm, C.maxx, C.nibiru][index % 5];
        let main, key;
        do {
            main = shuffle(deck.main, random);
            if (focus) {
                const opening = [focus];
                main.splice(main.indexOf(focus), 1);
                for (let filler = 0; filler < 4; filler++)
                    opening.push(main.splice(main.findIndex(id => !starters.has(id)), 1)[0]);
                main.unshift(...opening);
            }
            key = main.slice(0, 5).sort((a, b) => a - b).join(',');
        } while (seen.has(key));
        seen.add(key);
        const opposingDeck = Array(40).fill(89631139);
        if (pressure) opposingDeck[0] = pressure;
        result.push({ ...deck, main, pressure, id: index + 1,
            draw_seed: Math.floor(random() * 2 ** 32), opponent: { main: opposingDeck, extra: [] } });
    }
    return { train: result.slice(0, count), holdout: result.slice(count) };
}

function moveTrack(msg, state) {
    const fromHand = msg.previous.controller === 0 && msg.previous.location === 2;
    const toHand = msg.current.controller === 0 && msg.current.location === 2;
    const [entry] = fromHand ? state.handOrigins.splice(msg.previous.sequence, 1) : [];
    if (toHand) state.handOrigins.splice(msg.current.sequence, 0, entry || { code: msg.code });
}

function shuffleHand(msg, state) {
    const entries = [...state.handOrigins];
    state.handOrigins = msg.cards.map(code => {
        const index = entries.findIndex(entry => entry.code === code);
        return index < 0 ? { code } : entries.splice(index, 1)[0];
    });
}

function track(msg, state, name) {
    if (name === 'NewTurn') {
        state.turn += 1;
        state.turnPlayer = msg.player;
    }
    if (name === 'NewPhase') state.phase = msg.phase;
    if (name === 'Chaining') {
        state.chains.set(msg.chainCount, { code: msg.code, player: msg.controller, desc: msg.desc, location: msg.location });
        if (msg.controller === 0) state.followed.push([msg.code, msg.desc]);
        if (msg.controller === 1) state.pressureSeen = msg.code;
    }
    if (name === 'ChainSolving') state.solving = msg.chainCount;
    if (name === 'ChainSolved') {
        const source = state.chains.get(msg.chainCount);
        if (source?.player === 0 && source.code === C.saryuja)
            state.saryujaRetained += state.handOrigins.filter(entry => entry.source === source).length;
        state.solving = null;
    }
    if (name === 'ChainEnd') state.chains.clear();
    if (['Summoning', 'SpSummoning'].includes(name) && msg.controller === 0) state.pending++;
    if (['Summoned', 'SpSummoned'].includes(name)) {
        state.summons += state.pending;
        state.pending = 0;
    }
    if (name === 'Hint' && msg.player === 0 && msg.type === 3) state.hint = msg.desc;
    if (name === 'Draw') {
        const source = state.chains.get(state.solving);
        if (msg.player === 0) {
            state.handOrigins.push(...msg.cards.map(code => ({ code, source })));
            if (source?.code === C.dugares && msg.count === 2) state.dugaresDraws++;
        } else if (state.turn === 1) state.enemyDraws += msg.count;
    }
    if (name === 'Move') moveTrack(msg, state);
    if (name === 'ShuffleHand' && msg.player === 0) shuffleHand(msg, state);
}

function boardScore(duel, state) {
    const { QUERY_CODE, QUERY_POSITION, QUERY_STATUS, QUERY_EQUIP_CARD } = P.OcgcoreCommonConstants;
    const monsters = query(duel, 0, 4, QUERY_CODE | QUERY_POSITION | QUERY_STATUS);
    const ids = monsters.map(c => c.code);
    const spells = query(duel, 0, 8, QUERY_CODE | QUERY_POSITION | QUERY_EQUIP_CARD);
    const set = spells.filter(c => c.position & 10).map(c => c.code);
    const grave = query(duel, 0, 16).map(c => c.code);
    const hand = query(duel, 0, 2).map(c => c.code);
    const enabled = id => monsters.some(c => c.code === id && !(c.status & 1) && (c.position & 5));
    const george = monsters.find(c => c.code === C.george);
    const amazonBodies = monsters.filter(c => c.position & 5 && bodies.has(c.code)).length;
    const revivable = grave.some(id => bodies.has(id) && ![C.sword, C.empress].includes(id));
    const openZones = monsters.filter(c => c.sequence < 5).length < 5;
    const canSummonFromDeck = query(duel, 0, 1).some(c => bodies.has(c.code));
    const georgeEquipped = enabled(C.george) && spells.some(c => c.equipCard?.location === 4 &&
        c.equipCard.controller === 0 && c.equipCard.sequence === george.sequence);
    const interactions = {
        baronne: enabled(C.baronne) ? 150 : 0,
        georgeSpellTrap: georgeEquipped ? 100 : 0,
        georgeGraveLock: georgeEquipped ? 100 : 0,
        // Swordmaster may tribute itself when an opposing Effect Monster is available.
        sword: enabled(C.sword) ? 100 : 0,
        rescue: set.includes(C.rescue) && openZones && (hand.some(id => bodies.has(id)) ||
            canSummonFromDeck) ? 100 : 0,
        formation: set.includes(C.formation) && revivable && amazonBodies > 0 && openZones ? 100 : 0,
    };
    const draw = 75 * state.dugaresDraws + 50 * state.saryujaRetained;
    const followUp = hand.filter(id => bodies.has(id) || starters.has(id)).length
        + Number(revivable && set.includes(C.formation)) + Number(grave.includes(C.rescue));
    const boardPoints = Object.values(interactions).reduce((sum, score) => sum + score, draw);
    return { points: boardPoints - 50 * state.enemyDraws, boardPoints,
        interactions, draw, drawDetails: { dugares: state.dugaresDraws, saryujaRetained: state.saryujaRetained },
        followUp, board: ids, set, grave, hand, summons: state.summons,
        enemyDraws: state.enemyDraws, pressureSeen: state.pressureSeen, effects: state.followed };
}

function chooseEnemy(msg, state, name) {
    if (name === 'SelectChain') {
        const last = [...state.chains.values()].at(-1);
        const choice = msg.chains.find(c => c.forced) ||
            msg.chains.find(c => c.code === state.pressure && !state.enemyUsed &&
                (c.code === C.nibiru ? state.summons >= 5 : c.code === C.maxx ? state.phase >= 4 : last?.player === 0));
        if (choice?.code === state.pressure) state.enemyUsed = true;
        return msg.prepareResponse(choice);
    }
    if (name === 'SelectEffectYn' || name === 'SelectYesNo') return msg.prepareResponse(false);
    if (name === 'SelectCard') return msg.prepareResponse(msg.cards.slice(0, msg.min));
    return engineOptions(msg, state, policy)[0]?.response;
}

function msgOptionsContainDugares(choice, state) {
    const value = Number(choice.key);
    return state.lastIdle === C.dugares && value >= C.dugares * 16 && value <= C.dugares * 16 + 2;
}

function scoreAction(choice, state, name, visible) {
    const [type, location, desc] = choice.key.split('/');
    const source = state.chains.get(state.solving) || [...state.chains.values()].at(-1);
    if (name === 'SelectIdleCmd') {
        if (choice.key === 'end') return state.enemyDraws >= 3 ? 35 : 3;
        const id = choice.card;
        switch (Number(type)) {
        case P.IdleCmdType.SUMMON: return starters.has(id) ? 40 : 0.08;
        case P.IdleCmdType.SPSUMMON:
            if ([C.baronne, C.george].includes(id)) return 70;
            if (id === C.prism) return 44;
            if (id === C.saryuja) return visible.grave.some(id => preferredTuners.has(id)) ? 9 : 25;
            if (id === C.dugares) return 35;
            return extraLines.has(id) ? 18 : 2;
        case P.IdleCmdType.SSET: return interactionCards.has(id) ? 15 : 0.1;
        case P.IdleCmdType.MSET:
        case P.IdleCmdType.REPOS: return 0.05;
        case P.IdleCmdType.ACTIVATE:
            if (handTraps.has(id)) return 0.05;
            if (id === C.dugares || id === C.harpie) return 28;
            if (id === C.call && Number(location?.split(':')[2]) === 16) return 0.025;
            if (id === C.curse || id === C.chief || id === C.call || id === C.rota || id === C.mirror) return 45;
            if (id === C.rescue) return visible.bodies >= 4 ? 0.4 : 5;
            if (id === C.formation) return 1;
            return extraLines.has(id) ? 12 : fixedWeight(Boolean(desc), 3);
        default: return 1;
        }
    }
    if (name === 'SelectChain') {
        if (choice.key === 'pass') return 20;
        const id = Number(choice.key.split(':')[0]);
        if (source?.player === 1 && handTraps.has(id)) return 45;
        if (id === C.harpie) return source?.code === C.curse ? 50 : 7;
        if (interactionCards.has(id) || handTraps.has(id)) return 0.6;
        return bodies.has(id) || extraLines.has(id) ? 30 : 4;
    }
    if (name === 'SelectEffectYn' || name === 'SelectYesNo') return choice.key.endsWith(':true') ? 16 : 1;
    if (name === 'SelectOption') {
        if (msgOptionsContainDugares(choice, state)) return choice.key === String(C.dugares * 16) ? 30 : 1;
        if (source?.code === C.call) return choice.key === '1190' ? 20 : 1;
    }
    if (name === 'SelectPlace' && source && [C.curse, C.mirror, C.formation].includes(source.code)) {
        const sheep = visible.field.find(c => [C.sheep, C.sheep - 1].includes(c.code) && c.sequence >= 5);
        if (sheep) {
            const linked = sheep.sequence === 5 ? [0, 2] : [2, 4];
            const [, zone, sequence] = choice.key.split(':').map(Number);
            if (zone === 4 && linked.includes(sequence)) return 32;
        }
    }
    if (!choice.selectedCards?.length) return choice.weight;
    let weight = 1;
    for (const card of choice.selectedCards) {
        if (card.location === 64 && card.code === C.sword) weight += 25;
        if (source?.code === C.prism && card.code === C.shrimp) weight += 35;
        if (source?.code === C.george && card.code === C.legion) weight += 35;
        if (state.hint === HINTMSG_DISCARD && source?.code === C.dugares && preferredTuners.has(card.code) &&
            !visible.grave.some(id => preferredTuners.has(id))) weight += 40;
        if (state.hint === HINTMSG_SPSUMMON && [C.sheep, C.sheep - 1].includes(source?.code) &&
            preferredTuners.has(card.code)) weight += 40;
        if (state.hint === HINTMSG_LMATERIAL && state.lastIdle === C.prism && [C.sheep, C.sheep - 1].includes(card.code))
            weight += 14;
        if (state.hint === HINTMSG_LMATERIAL && state.lastIdle === C.george && [C.prism, C.shrimp].includes(card.code))
            weight += 20;
        if (state.hint === HINTMSG_XMATERIAL && state.lastIdle === C.dugares && card.code === C.fighter)
            weight += 12;
        if (card.location === 1 && source?.code === C.curse && card.code === C.fighter) weight += 35;
        if (card.location === 1 && [C.call, C.rota].includes(source?.code)) {
            if (card.code === C.chief && !visible.hand.includes(C.chief)) weight += 20;
            if (card.code === C.curse && !visible.hand.includes(C.curse)) weight += 25;
        }
        if (card.location === 1 && interactionCards.has(card.code)) weight += 16;
    }
    return weight;
}

function standardChooser() {
    const I = P.IdleCmdType;
    const S = P.OcgcoreScriptConstants;
    // This single-Curse guide supplies one search branch. Every action still requires an engine prompt.
    const plan = [
        [I.SUMMON, C.curse], [I.ACTIVATE, C.chief], [I.ACTIVATE, C.rescue, 8],
        [I.ACTIVATE, C.mirror, 8], [I.ACTIVATE, C.harpie], [I.SPSUMMON, C.dugares],
        [I.ACTIVATE, C.dugares], [I.ACTIVATE, C.rescue, 16], [I.SPSUMMON, C.sheep],
        [I.ACTIVATE, C.mirror, 16], [I.SPSUMMON, C.prism], [I.SPSUMMON, C.george],
        [I.SPSUMMON, C.baronne], [I.SSET, C.formation],
    ];
    const summonTriggers = {
        1: [[C.curse, 0]], 2: [[C.chief, 0]], 3: [[C.fighter, 1]], 5: [[C.harpie, 2]],
        10: [[C.sword, 0], [C.sheep, 0], [C.sheep - 1, 0]], 11: [[C.prism, 0]],
        12: [[C.george, 0]], 13: [[C.legion, 1]],
    };
    let stage = 0;
    let tuner;
    return ({ msg, state, visible, name, choices }) => {
        const source = state.chains.get(state.solving)?.code;
        const select = (code, zone) => choices.find(o => o.selectedCards?.some(c =>
            c.code === code && (zone == null || c.location === zone)));
        const finish = msg.finishable && choices.find(o => o.key === 'done');
        const material = (code, zone) => !msg.unselectableCards?.some(c => c.code === code) && select(code, zone);
        if (name === 'SelectIdleCmd') {
            if (stage === plan.length) return choices.find(o => o.key === 'end');
            const [type, card, location] = plan[stage];
            const action = choices.find(o => o.key.startsWith(type + '/') && o.card === card &&
                (location == null || Number(o.key.split('/')[1].split(':')[2]) === location));
            if (action) stage++;
            return action;
        }
        if (name === 'SelectChain') {
            if (stage === 10) {
                const sword = choices.find(o => o.key === `${C.sword}:${C.sword * 16}`);
                if (sword) return sword;
            }
            return choices.find(o => {
                const [code, desc] = o.key.split(':').map(Number);
                return (stage === 8 && code === C.fighter && desc === C.fighter * 16) ||
                    (stage === 10 && (code === C.sheep || code === C.sheep - 1));
            }) || choices.find(o => o.key === 'pass') || choices[0];
        }
        if (name === 'SelectEffectYn') {
            const expected = summonTriggers[stage]?.some(([id, index]) => msg.code === id &&
                (msg.desc === 221 || !msg.desc || msg.desc === id * 16 + index));
            return expected ? choices.find(o => o.key.endsWith(':true')) : undefined;
        }
        if (name === 'SelectYesNo') return undefined;
        if (name === 'SelectOption' && stage === 7)
            return choices.find(o => o.key === String(C.dugares * 16));
        if (name === 'SelectPlace' && source === C.mirror && stage === 10) {
            const sheep = visible.field.find(c => c.code === C.sheep || c.code === C.sheep - 1);
            if (sheep?.sequence >= 5) {
                const zones = sheep.sequence === 5 ? ['0:4:0', '0:4:2'] : ['0:4:2', '0:4:4'];
                return choices.find(o => zones.includes(o.key)) || choices[0];
            }
        }
        if (name === 'SelectCard' || name === 'SelectUnselectCard') {
            switch (stage) {
            case 1: if (source === C.curse) return select(C.chief, 1); break;
            case 2: if (source === C.chief) return select(C.rescue, 1); break;
            case 3:
                if (source === C.rescue) return select(C.fighter, 1);
                if (source === C.fighter) return select(C.mirror, 1);
                if (state.hint === S.HINTMSG_RTOHAND) return select(C.chief, 4);
                break;
            case 4: if (source === C.mirror) return select(C.harpie, 1); break;
            case 5:
                if (source === C.harpie) {
                    return select(C.formation, 1) || select(C.mirror, 1);
                }
                if (state.hint === S.HINTMSG_RTOHAND) return select(C.curse, 4);
                break;
            case 6: return choices.find(o => o.selectedCards?.length === 2 &&
                o.selectedCards.some(c => c.code === C.harpie) && o.selectedCards.some(c => c.code === C.fighter));
            case 7:
                if (state.hint === S.HINTMSG_DISCARD) {
                    for (const id of preferredTuners) {
                        const discard = select(id, 2);
                        if (discard) {
                            tuner = id;
                            return discard;
                        }
                    }
                    return undefined;
                }
                break;
            case 8: if (source === C.rescue) return select(C.fighter, 16); break;
            case 9: return finish || material(C.dugares, 4) || material(C.fighter, 4);
            case 10:
                if (source === C.mirror) return select(C.sword, 64) || finish || material(C.chief, 2) || material(C.curse, 2);
                if (source === C.sword) return select(C.fighter, 1);
                if ((source === C.sheep || source === C.sheep - 1) && tuner) return select(tuner, 16);
                break;
            case 11:
                if (source === C.prism) return select(C.shrimp, 64);
                if (!source) return finish || material(C.sheep, 4) || material(C.sheep - 1, 4) || material(tuner, 4);
                break;
            case 12:
                if (source === C.george) return select(C.legion, 64);
                if (!source) return finish || material(C.prism, 4) || material(C.shrimp, 4);
                break;
            case 13: if (source === C.legion) return select(C.george, 4); break;
            }
        }
        return choices[0];
    };
}

function rollout(engine, input, { random, deadline, prefix = [], pivot = -1, model, reference = false,
    choose, record }) {
    const { core } = engine;
    const duel = openDuel(core, input);
    const state = { bot: 0, cardReader: engine.cardReader, turn: 0, phase: 0, chains: new Map(), handOrigins: [], followed: [],
        summons: 0, pending: 0, enemyDraws: 0,
        dugaresDraws: 0, saryujaRetained: 0, pressure: input.pressure, trace: [], declarations: [...new Set(input.main)],
        searchPolicy: policy };
    const wire = [], branches = [];
    try {
        for (let step = 0; step < 700 && Date.now() < deadline; step++) {
            for (const msg of duel.process().messages || []) {
                const name = nameOf(msg);
                if (['Retry', 'Win'].includes(name)) return null;
                track(msg, state, name);
                if (state.turn === 2) return { ...boardScore(duel, state), wire, branches };
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                let response;
                if (msg.responsePlayer() === 1) {
                    response = chooseEnemy(msg, state, name);
                    if (prefix[wire.length] && wire.length < pivot && wireOf(response) !== prefix[wire.length]) return null;
                } else {
                    const field = query(duel, 0, 4, 3);
                    const visible = { bodies: field.length, field, hand: query(duel, 0, 2).map(c => c.code),
                        grave: query(duel, 0, 16).map(c => c.code) };
                    const choices = optionsFor(msg, state).map(choice => {
                        const action = choice.key.replace(/:(\d+)(?=\/)/, '');
                        const features = [`action:${name}:${action}`, `bodies:${Math.min(4, visible.bodies)}:${name}:${action}`,
                            `hit:${state.pressureSeen || 0}:${name}:${action}`];
                        const weight = scoreAction(choice, state, name, visible);
                        return { ...choice, features, weight, action: wireOf(choice.response),
                            rank: Math.log(weight) + (model ? features.reduce((sum, key) => sum + (model[key] || 0), 0) : 0) };
                    }).sort((a, b) => b.rank - a.rank);
                    if (!choices.length) return null;
                    let chosen;
                    const offset = wire.length;
                    if (offset < pivot) chosen = choices.find(option => option.action === prefix[offset]);
                    else if (name === 'SelectPosition') chosen = summonPosition(msg, state, choices);
                    else if (choose) chosen = choose({ msg, state, visible, name, choices, offset });
                    else if (reference || model) chosen = choices[0];
                    else {
                        const alternatives = offset === pivot ? choices.filter(o => o.action !== prefix[offset]) : choices;
                        const pool = alternatives.length ? alternatives : choices;
                        let ticket = random() * pool.reduce((sum, c) => sum + c.weight, 0);
                        chosen = pool.find(c => (ticket -= c.weight) <= 0) || pool.at(-1);
                    }
                    if (!chosen) return null;
                    record?.({ msg, state, visible, name, choices, chosen, offset });
                    if (choices.length > 1 && name !== 'SelectPosition') branches.push({ offset, prefix: hash(wire.join('|')).slice(0, 24),
                        action: chosen.action, features: chosen.features });
                    if (name === 'SelectIdleCmd') {
                        state.lastIdle = chosen.card;
                        state.hint = undefined;
                    }
                    response = chosen.response;
                }
                if (response === undefined || response === null) return null;
                wire.push(wireOf(response));
                duel.setResponse(response);
            }
        }
        return null;
    } finally {
        duel.endDuel();
    }
}

function replay(engine, input, result) {
    if (!result) return false;
    const run = rollout(engine, input, { prefix: result.wire, pivot: Infinity, deadline: Date.now() + 3000 });
    return Boolean(run && run.points === result.points && run.wire.join('|') === result.wire.join('|') &&
        run.board.join(',') === result.board.join(',') && run.hand.join(',') === result.hand.join(','));
}

function search(engine, input, attempts, ms, standard = false) {
    const random = rng(input.draw_seed ^ 0xbd508497), deadline = Date.now() + ms;
    const runs = [], unsupported = {}, seen = new Set();
    let count = 0, guided;
    for (; count < attempts && Date.now() < deadline; count++) {
        const pool = runs.filter(run => run.branches.length).slice(0, 16);
        const useGuide = standard && count === 1 && input.main.slice(0, 5).includes(C.curse);
        const parent = count % 4 && !useGuide ? pool[Math.floor(random() ** 2 * pool.length)] : undefined;
        const pivot = parent?.branches[Math.floor(random() * parent.branches.length)];
        try {
            const result = rollout(engine, input, { random, deadline, reference: count === 0,
                prefix: parent?.wire, pivot: pivot?.offset ?? -1, choose: useGuide ? standardChooser() : undefined });
            if (useGuide) guided = result;
            if (result && !seen.has(result.wire.join('|'))) {
                seen.add(result.wire.join('|'));
                runs.push(result);
                runs.sort(byDescending);
            }
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupported[error.prompt] = (unsupported[error.prompt] || 0) + 1;
        }
    }
    const replayed = runs.slice(0, 12).filter(run => replay(engine, input, run));
    return { attempts: count, settled: runs.length, replayed: replayed.length, unsupported,
        best: replayed[0], guided: guided && replay(engine, input, guided) ? guided : undefined, pairs: pairsFrom(replayed) };
}

function pairsFrom(runs) {
    const groups = new Map();
    for (const run of runs) for (const branch of run.branches) {
        if (!groups.has(branch.prefix)) groups.set(branch.prefix, new Map());
        const group = groups.get(branch.prefix);
        if (!group.has(branch.action)) group.set(branch.action, { features: branch.features, points: run.points });
    }
    return [...groups.values()].flatMap(group => {
        const [best, ...others] = group.values();
        return others.filter(other => other.points < best.points)
            .map(other => ({ high: best.features, low: other.features, margin: best.points - other.points }));
    });
}

function fit(rows, seed) {
    const weights = {};
    const random = rng(seed);
    for (let epoch = 0; epoch < 8; epoch++) for (const row of shuffle(rows, random)) {
        const high = row.high.filter(key => !row.low.includes(key));
        const low = row.low.filter(key => !row.high.includes(key));
        const score = keys => keys.reduce((sum, key) => sum + (weights[key] || 0), 0);
        const update = Math.min(0.40, row.margin / 150) / (1 + Math.exp(score(high) - score(low)));
        for (const [keys, sign] of [[high, 1], [low, -1]]) {
            for (const key of keys) weights[key] = Math.max(-2.5, Math.min(2.5, (weights[key] || 0) + sign * update));
        }
    }
    return weights;
}

function serialize(file, json) {
    fs.writeFileSync(file + '.tmp', JSON.stringify(json, null, 2) + '\n');
    fs.renameSync(file + '.tmp', file);
}

function config() {
    const options = {
        fixture: { type: 'string' }, data: { type: 'string', multiple: true }, deck: { type: 'string' },
        output: { type: 'string' }, cases: { type: 'string', default: '120' },
        holdout: { type: 'string', default: '20' }, attempts: { type: 'string', default: '128' },
        'standard-route': { type: 'boolean' }, 'case-ms': { type: 'string', default: '3000' }, 'focus-starter': { type: 'string' }, seed: { type: 'string', default: '20261127' },
    };
    const { values } = parseArgs({ options });
    for (const key of ['fixture', 'data', 'deck', 'output']) {
        if (!values[key]) throw new Error('Required --' + key);
    }
    for (const key of ['cases', 'holdout', 'attempts', 'case-ms', 'seed']) {
        values[key] = Number(values[key]);
        if (!Number.isSafeInteger(values[key]) || values[key] < 1) throw new Error('Invalid --' + key);
    }
    if (values['focus-starter'] !== undefined) {
        values['focus-starter'] = Number(values['focus-starter']);
        if (!starters.has(values['focus-starter'])) throw new Error('Unknown starter');
    }
    return values;
}

async function main() {
    const settings = config();
    const deckBytes = fs.readFileSync(settings.deck);
    const deck = readDeck(deckBytes.toString('utf8'));
    const engine = await loadEngine(path.resolve(settings.fixture), settings.data.map(value => path.resolve(value)));
    for (const id of new Set([...deck.main, ...deck.extra, 89631139])) {
        const props = engine.cardReader(id);
        if (!props || (!(props.type & 0x10) && ![id, props.alias, props.ruleCode].filter(Boolean)
            .some(code => engine.scriptReader('script/c' + code + '.lua'))))
            throw new Error('Missing card or script: ' + id);
    }
    if (settings['focus-starter'] && !deck.main.includes(settings['focus-starter']))
        throw new Error('Focused starter is absent from deck');
    const cases = buildCases(deck, settings.cases, settings.holdout, settings.seed, settings['focus-starter']);
    fs.mkdirSync(settings.output, { recursive: true });
    const at = file => path.join(settings.output, file);
    const manifest = { seed: settings.seed, deckSha256: hash(deckBytes),
        trainerSha256: hash(fs.readFileSync(__filename)), settings, cases };
    if (fs.existsSync(at('inputs.json'))) {
        const previous = JSON.parse(fs.readFileSync(at('inputs.json'), 'utf8'));
        if (JSON.stringify(previous) !== JSON.stringify(manifest)) throw new Error('Training configuration changed');
    } else serialize(at('inputs.json'), manifest);
    const training = [];
    for (const input of cases.train) {
        const file = at(`case-${input.id}.json`);
        const row = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) :
            search(engine, input, settings.attempts, settings['case-ms'], settings['standard-route']);
        if (engine.engineErrors.size) throw new Error([...engine.engineErrors].slice(0, 8).join('\n'));
        if (!fs.existsSync(file)) serialize(file, row);
        training.push(row);
        console.log(JSON.stringify({ id: input.id, settled: row.settled, attempts: row.attempts, replayed: row.replayed,
            score: row.best?.points, pairs: row.pairs.length, pressure: input.pressure, unsupported: row.unsupported }));
    }
    const pairs = training.flatMap(row => row.pairs);
    if (!pairs.length) throw new Error('No replay-verified ranking pairs');
    const weights = fit(pairs, settings.seed);
    serialize(at('candidate.json'), { version: 1, objective: settings['standard-route'] ? 'amazoness-first-turn-standard-v1' : 'amazoness-first-turn-pilot-v3',
        deckSha256: manifest.deckSha256, trainerSha256: manifest.trainerSha256, pairs: pairs.length, weights });
    const evaluation = cases.holdout.map(input => {
        const run = opts => {
            try {
                return rollout(engine, input, { ...opts, deadline: Date.now() + settings['case-ms'] });
            } catch (error) {
                if (!(error instanceof UnsupportedPrompt)) throw error;
                return null;
            }
        };
        const rule = run({ reference: true }), candidate = run({ model: weights });
        const oracle = search(engine, input, settings.attempts, settings['case-ms'], settings['standard-route']);
        if (engine.engineErrors.size) throw new Error([...engine.engineErrors].slice(0, 8).join('\n'));
        serialize(at(`holdout-${input.id}.json`), { rule, candidate, reference: oracle.best, guided: oracle.guided });
        return { id: input.id, pressure: input.pressure, rule: rule?.points, candidate: candidate?.points,
            reference: oracle.best?.points, guided: oracle.guided?.points, ruleVerified: replay(engine, input, rule),
            candidateVerified: replay(engine, input, candidate) };
    });
    const summary = { training: { hands: training.length, attempts: training.reduce((n, row) => n + row.attempts, 0),
        uniqueSettled: training.reduce((n, row) => n + row.settled, 0),
        verifiedBranches: training.reduce((n, row) => n + row.replayed, 0), pairs: pairs.length },
    holdout: evaluation, weights: Object.keys(weights).length };
    serialize(at('summary.json'), summary);
    console.log(JSON.stringify(summary));
}

module.exports = { C, readDeck, rollout, replay, loadEngine, standardChooser };

if (require.main === module) main().catch(error => {
    console.error(error.stack);
    process.exitCode = 1;
});
