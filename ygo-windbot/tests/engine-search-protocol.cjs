'use strict';

const P = require('ygopro-msg-encode');
const coreApi = require('koishipro-core.js');

const nameOf = msg => msg.constructor.name.slice('YGOProMsg'.length);

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

function engineOptions(msg, state, policy) {
    const { idleWeight, choicePriority, normalize, handTraps, includeSequence } = policy;
    const index = i => P.IndexResponse(i);
    const name = nameOf(msg);
    const option = (key, response, weight = 1) => ({ key, response, weight });
    const cardKey = c => [c.code, c.controller, c.location, ...(includeSequence ? [c.sequence] : [])].join(':');
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
                card: normalize(c.code),
            })));
            if (msg.canEp) result.push(option('end', msg.prepareResponse(I.TO_EP)));
            if (msg.canBp) result.push(option('battle', msg.prepareResponse(I.TO_BP)));
            return result;
        }
        case 'SelectChain': {
            const forced = msg.chains.some(c => c.forced);
            return [...msg.chains.flatMap((c, i) => !forced || c.forced
                ? [{ ...option([c.code, c.desc].join(':'), msg.prepareResponse(index(i)),
                    handTraps.has(c.code) && !state.chains.size ? 1 : 16),
                    chainLocation: c.location, chainSequence: c.sequence }] : []),
            ...(!forced ? [option('pass', msg.prepareResponse(), msg.chains.some(c => !handTraps.has(c.code)) ? 1 : 10)] : [])];
        }
        case 'SelectEffectYn':
        case 'SelectYesNo':
            return [true, false].map(yes =>
                option([msg.code || 0, msg.desc || 0, yes].join(':'), msg.prepareResponse(yes), yes ? 8 : 1));
        case 'SelectUnselectCard':
            return [...msg.selectableCards.map((c, i) => ({
                ...option(cardKey(c), msg.prepareResponse(index(i)), choicePriority(c, state)),
                selectedCards: [c],
            })),
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
                const weight = cards.length ? cards.reduce((sum, c) =>
                    sum + (choicePriority(c, state)), 0) : 1;
                return { ...option(cards.map(cardKey).sort().join(','), msg.prepareResponse(group.map(index)), weight),
                    selectedCards: cards };
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

function openDuel(core, input, coach) {
    const duel = core.createDuelV2(coach?.seed || [input.draw_seed >>> 0, 0, 0, 0, 0, 0, 0, 0]);
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
    duel.startDuel(coach?.options ?? ((5 << 16) | coreApi.OcgcoreDuelOptionFlag.PseudoShuffle));
    return duel;
}

module.exports = { engineOptions, nameOf, openDuel, selections, UnsupportedPrompt };
