'use strict';

const P = require('ygopro-msg-encode');

const card = {
    spoon: 42544773, king: 100274003, sorcerer: 36099130, cendrillon: 72498838,
    verre: 22623509, aruru: 100274004, genie: 64756282, souls: 97631303,
    tears: 73664385, nation: 34041788, mastery: 38943357, arrival: 39123673,
    descent: 100274028, power: 101307067, outlaw: 24203749,
    maiden: 34755994, witch: 100278018, corozo: 66532962, savage: 27548199,
    dragon: 37260677, gottoms: 52352005, wayne: 83810690, four: 27519978,
    selene: 45819647, moon: 20714553, dark: 59400890, emeral: 581014, littleKnight: 29301450,
    droll: 94145021,
};
const trap = {
    maxx: 23434538, ash: 14558127, imperm: 10045474, veiler: 97268402,
    nibiru: 27204311, impulse: 40366667, purge: 97045737, princess: 65302903,
    ogre: 59438930, belle: 73642296, fuwalos: 42141493, purulia: 84192580,
};
const drawTraps = new Set([trap.maxx, trap.fuwalos, trap.purulia]);
const fieldStops = new Set([trap.imperm, trap.veiler, trap.ogre]);
const negate = new Set([...fieldStops, trap.ash, trap.purge, trap.belle, trap.impulse]);
const ownDefensiveEffects = new Set([...negate, ...drawTraps, card.savage, card.dragon, card.outlaw]);
const material = new Set([card.sorcerer, card.verre, card.cendrillon, card.genie, card.souls,
    card.corozo, card.wayne, card.aruru, card.king, card.witch, card.dark, card.moon]);
const activeSpells = new Set([card.nation, card.mastery, card.arrival, card.tears, card.descent, card.power]);
const keyEffects = new Set([card.witch, card.sorcerer, card.cendrillon, card.verre, card.king,
    card.maiden, card.four, card.selene, card.gottoms, card.savage, card.dark, card.genie]);
const statusDisabled = 0x1;

function settledScore(board, lost, extra, draws, spells = []) {
    const available = board.filter(c => c.position & 5 && !(c.status & statusDisabled));
    const savage = available.some(c => c.code === card.savage && c.counters?.some(x => x.type === 0x4b && x.count));
    // The Dragon can also send itself to pay the cost.
    const dragon = available.some(c => c.code === card.dragon);
    const little_knight = available.some(c => (c.alias || c.code) === card.littleKnight);
    // Moon can return the equipped Spellcaster during the opponent's main phase.
    const droll_access = available.some(c => (c.alias || c.code) === card.moon) &&
        spells.some(c => c.owner === 0 && (c.alias || c.code) === card.droll && (c.position & 5) &&
            c.equipCard?.controller === 0 && c.equipCard.location === 4);
    const net = Math.max(0, lost + extra - draws);
    const rip = net >= 6 && lost >= 5 && extra >= 1 ? 150 : [0, 10, 25, 45, 70, 100][Math.min(5, net)];
    return { score: rip + 10 * savage + 9 * dragon + 6 * little_knight + 10 * droll_access,
        rip, savage, dragon, little_knight, droll_access, net };
}

function ownPolicy(mode, preferEarlySavage) {
    const code = c => c?.alias || c?.code;
    const earlySavage = preferEarlySavage && mode % 6 === 0;
    const normalFree = mode % 4 !== 0;
    const preferSavage = mode % 4 === 0;
    const used = (state, id) => state.events.some(e => e[0] === 'effect' && e[2] === 0 && e[3] === id);
    return {
        normalize: id => id,
        handTraps: drawTraps,
        idleWeight(type, target, state) {
            const id = code(target);
            const I = P.IdleCmdType;
            const field = state.board;
            if (type === I.SUMMON) {
                if (id === card.outlaw) return state.trapCount ? 50 : 3;
                if (id === card.sorcerer)
                    return normalFree && !used(state, card.genie) && state.hand.some(id =>
                        [card.verre, card.king, card.nation, card.mastery, card.tears].includes(id)) ? 1.2 : 65;
                return id === card.cendrillon ? normalFree && !used(state, card.genie) ? 4 : 32 : id === card.genie ? 3 : 0.6;
            }
            if (type === I.SPSUMMON) {
                if (id === card.gottoms) return 140;
                if (id === card.wayne) return field.includes(card.wayne) || state.grave.includes(card.wayne) ? 2 : 70;
                if (id === card.four) return state.grave.includes(card.gottoms) ? 140 : 0.5;
                if (id === card.savage || id === card.dragon) {
                    if (earlySavage && id === card.savage && state.summons <= 4) return 150;
                    return preferSavage ? 72 : field.includes(card.wayne) ? 10 : 36;
                }
                if (id === card.corozo) return 39;
                if (id === card.witch || id === card.dark) return 57;
                if (id === card.selene) return state.spellCount >= 3 ? 49 : 8;
                if (id === card.moon) return field.includes(card.dark) && field.includes(card.cendrillon) ? 64 : 25;
                if (id === card.maiden) return field.includes(card.verre) && !used(state, card.genie) ? 120 : 16;
                if (id === card.emeral) return mode % 3 === 2 ? 20 : 4;
                if (id === card.king) return 70;
                return 2;
            }
            if (type === I.ACTIVATE) {
                if (id === card.gottoms) return 170;
                if (id === card.four && state.grave.includes(card.gottoms)) return 85;
                if (id === card.four) return 7;
                if (id === card.spoon || id === card.sorcerer || id === card.maiden || id === card.witch) return 75;
                if (id === card.dragon) return 12;
                if (id === card.genie) return target.location === 4 ? 9 : 95;
                if (id === card.tears && normalFree && !used(state, card.genie)) return state.board.includes(card.maiden) ? 80 : 1.2;
                if (material.has(id) || id === card.selene || id === card.descent || id === card.tears) return 45;
                if (activeSpells.has(id)) return id === card.power ? 9 : 34;
                return 2;
            }
            if (type === I.SSET && id === card.tears && mode % 3 === 2) return field.length ? 4 : 0.5;
            if (type === I.SSET && id === card.descent && !field.includes(card.dragon) &&
                field.some(id => [card.dark, card.corozo, card.witch].includes(id))) return 12;
            return 0.25;
        },
        choicePriority(target, state) {
            const id = code(target);
            const source = state.chainSource || state.lastIdle;
            const hint = state.selectionHint;
            if (source === card.gottoms && target.location === 4)
                return id === card.wayne ? 1000 : 2;
            if (source === card.four && target.location === 16)
                return id === card.gottoms ? 140 : id === card.wayne ? 90 : 7;
            if (hint === 506) {
                if (source === card.spoon) {
                    if (earlySavage && !state.hand.includes(card.sorcerer)) return id === card.sorcerer ? 150 : 7;
                    return id === card.king ? 55 : id === card.sorcerer ? 18 : 8;
                }
                if (source === card.king) {
                    if (id === card.verre && (normalFree || earlySavage)) return 120;
                    return id === card.sorcerer ? 56 : id === card.cendrillon ? 21 : 8;
                }
                if (source === card.maiden) {
                    if (earlySavage && !used(state, card.king) && !state.hand.includes(card.king))
                        return id === card.king ? 150 : 8;
                    if (normalFree && used(state, card.genie)) return id === card.sorcerer ? 110 : id === card.aruru ? 40 : 12;
                    return id === card.cendrillon ? 44 : id === card.aruru ? 31 : id === card.sorcerer ? 20 : 8;
                }
                if (source === card.cendrillon && used(state, card.genie)) return id === card.descent ? 130 : 10;
                if (source === card.cendrillon || source === card.verre) return id === card.tears ? 70 : id === card.descent ? 28 : 10;
                if (source === card.mastery) return id === card.nation ? 60 : 9;
                if (source === card.arrival) return id === card.mastery ? 60 : 8;
            }
            if (hint === 504 && target.location === 2)
                return material.has(id) || activeSpells.has(id) || id === card.spoon ? 1 : 14;
            if (hint === 509 && target.location === 16) {
                if (id === card.sorcerer) return 32;
                if (id === card.cendrillon || id === card.verre) return 25;
            }
            if (hint === 509 && [1, 2].includes(target.location)) {
                if (source === card.genie) return id === card.cendrillon ? 120 : id === card.sorcerer ? 17 : 9;
                return id === card.verre ? 26 : id === card.sorcerer ? 20 : 7;
            }
            if (target.location === 64) {
                if (source === card.descent) {
                    return id === card.dragon ? (mode % 3 === 0 ? 74 : 35) : id === card.witch ? 38 : 12;
                }
                return id === card.maiden ? 45 : id === card.witch ? 34 : id === card.dragon ? 8 : 5;
            }
            if (hint === 504 && target.location === 1 && source === card.tears)
                return id === card.genie && !used(state, card.genie) ? 150 : id === card.droll ? 32 : id === card.aruru ? 38 : 5;
            if (source === card.moon && target.location === 16 && hint === 518 && id === card.droll)
                return 30;
            if (hint === 504 && target.location === 1 && (source === card.souls || state.lastIdle === card.souls))
                return id === card.aruru ? 135 : 8;
            if (hint === 533 || hint === 512 || hint === 511 || hint === 500 || hint === 503) {
                if (id === card.gottoms || id === card.savage || id === card.dragon) return 0.1;
                return id === card.corozo || id === card.maiden || id === card.moon ? 7 : 4;
            }
            return material.has(id) || keyEffects.has(id) ? 6 : 3;
        },
    };
}

function opponentChoice(msg, state, options, target) {
    const prior = [...state.chains.values()].at(-1);
    const source = prior?.player === 0 ? prior : null;
    switch (msg.constructor.name) {
    case 'YGOProMsgSelectChain': {
        const forced = msg.chains.find(c => c.forced);
        if (forced) return msg.prepareResponse(forced);
        let best, priority = 0;
        for (const choice of msg.chains) {
            const id = choice.code;
            if (choice.location !== 2 || state.chosen.has(id)) continue;
            if (target?.card && source?.code !== target.card) continue;
            if (target?.location && source?.location !== target.location) continue;
            if (target?.effect && source?.desc !== target.effect) continue;
            let weight = 0;
            if (drawTraps.has(id) && state.turn === 1 && state.phase < 512) weight = 160;
            else if (id === trap.princess && state.turn === 1 && !source &&
                !state.chosen.has(trap.purge)) weight = 130;
            else if (id === trap.nibiru && state.summons >= 5 && state.turn === 1) weight = 125;
            else if (source && negate.has(id)) {
                if (fieldStops.has(id) && source.location !== 4 && !(id === trap.ogre && source.location === 8))
                    continue;
                if (fieldStops.has(id) && !keyEffects.has(source.code) && source.code !== card.wayne)
                    continue;
                weight = id === trap.ash ? 103 : id === trap.purge ? 108 : 100;
            }
            if (weight > priority) [best, priority] = [choice, weight];
        }
        if (best) {
            state.chosen.add(best.code);
            state.target = source?.code;
            state.trapActions.push([best.code, source?.code || 0, state.summons]);
        }
        return msg.prepareResponse(best);
    }
    case 'YGOProMsgSelectCard': {
        const ordered = [...msg.cards].sort((a, b) => targetWeight(b) - targetWeight(a));
        return msg.prepareResponse(ordered.slice(0, msg.min));
    }
    case 'YGOProMsgSelectUnselectCard':
        return msg.prepareResponse(msg.finishable ? null : msg.selectableCards[0]);
    case 'YGOProMsgSelectYesNo':
    case 'YGOProMsgSelectEffectYn':
        return msg.prepareResponse(true);
    default:
        return options(msg, state)[0]?.response;
    }

    function targetWeight(c) {
        if (c.controller === 0) return c.code === state.target ? 100 : keyEffects.has(c.code) ? 30 : 10;
        if (c.location === 1 && c.code === trap.purge) return 20;
        if (c.location === 2 && c.code === trap.princess) return 10;
        return c.location === 2 && !Object.values(trap).includes(c.code) ? 5 : 0;
    }
}

module.exports = { card, trap, drawTraps, ownDefensiveEffects, settledScore, ownPolicy, opponentChoice };
