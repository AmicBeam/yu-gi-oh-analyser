'use strict';

const P = require('ygopro-msg-encode');
const { card, trap, drawTraps } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');

const DRAW = 1, MAIN_ONE = 4, MAIN_TWO = 256, MZONE = 4, SZONE = 8, EXTRA = 64;
const isMain = phase => phase === MAIN_ONE || phase === MAIN_TWO;
const code = c => c?.alias || c?.code || 0;
const faceUp = c => c && (c.position & 5) !== 0;
const active = c => faceUp(c) && !(c.status & 1);
const ash = new Set([trap.ash, 14558128]);
const droll = new Set([card.droll, 94145022]);
const magistus = new Set([card.spoon, card.king, card.sorcerer, card.cendrillon, card.aruru,
    card.maiden, card.witch, card.moon, card.dragon]);
const fusion = new Set([card.witch, card.corozo, card.dark, card.dragon]);
const protectedBodies = new Set([card.dragon, card.savage, card.littleKnight, card.moon]);
const bigPressure = new Set([...drawTraps, trap.nibiru]);
const DESC_SUMMON = card.descent * 16;
const MOON_RETURN = card.moon * 16 + 1;
const SP_REMOVE = card.littleKnight * 16 + 1;
const DRAGON_NEGATE = card.dragon * 16 + 1;
const SAVAGE_NEGATE = card.savage * 16 + 1;
const OUTLAW_NEGATE = card.outlaw * 16 + 1;
const endSetOrder = [trap.imperm, card.descent, card.tears];
const isEndSet = option => option.key.startsWith(P.IdleCmdType.SSET + '/') && endSetOrder.includes(option.card);

function effect(option) {
    const [id, desc] = option.key.split(':').map(Number);
    return { id, desc };
}

function hasOwnDroll(policy) {
    return policy.spells.some(c => faceUp(c) && droll.has(code(c)) && (c.owner == null || c.owner === policy.seat));
}

function canTurnIntoDragon(policy) {
    const openZone = policy.field.slice(0, 5).filter(Boolean).length < 5;
    return !policy.field.some(c => active(c) && code(c) === card.dragon) &&
        (policy.extra.includes(card.dragon) || policy.spells.some(c => faceUp(c) && code(c) === card.dragon)) &&
        (policy.field.some(c => faceUp(c) && fusion.has(code(c))) ||
            (openZone && policy.spells.some(c => faceUp(c) && fusion.has(code(c)))));
}

function chainRules(policy, options, guided) {
    // Forced chains are dictated by OCGCore, including trigger ordering.
    if (!options.some(option => option.key === 'pass')) return { options };
    const s = policy.state;
    const ownTurn = policy.own(policy.turnActor);
    const previous = [...s.chains.values()].at(-1);
    const enemyEffect = previous?.player === 1 ? previous : null;
    const monsterEffect = enemyEffect && enemyEffect.location !== SZONE &&
        (s.cardReader(enemyEffect.code)?.type & 1);
    const enemyFieldEffect = monsterEffect && enemyEffect.location === MZONE;
    const source = enemyFieldEffect && policy.enemyField[enemyEffect.sequence];
    const sourceOnField = faceUp(source) && (source.code === enemyEffect.code || code(source) === enemyEffect.code);
    const urgent = enemyEffect && bigPressure.has(enemyEffect.code);
    const foeFaceUp = policy.enemyField.filter(faceUp).length;
    const freeDragonCost = policy.spells.some(c => faceUp(c) && magistus.has(code(c)));
    const ownDraw = policy.ownDrawPressure.size > 0;
    const ownDrawOnChain = [...s.chains.values()].some(link => link.player === 0 && drawTraps.has(link.code));

    function policyFor(option) {
        const { id, desc } = effect(option);
        if (id === card.verre && desc === card.verre * 16 + 1 &&
            policy.glassIsDisabled(policy.seat, option.chainLocation, option.chainSequence)) return [0];
        if (id === card.descent && desc === DESC_SUMMON && !ownTurn)
            return s.phase === DRAW && canTurnIntoDragon(policy) ? [95, 'draw-phase-dragon'] : [0];
        if (id === card.moon && desc === MOON_RETURN && !ownTurn)
            return isMain(s.phase) && hasOwnDroll(policy) && !ownDraw && !ownDrawOnChain ? [94, 'moon-droll'] : [0];
        if (id === card.tears) {
            if (enemyEffect && !policy.tearsReactiveUsed && policy.field.slice(0, 5).filter(Boolean).length < 5)
                return [79, 'tears-reactive'];
            return ownTurn ? null : [0];
        }
        if (drawTraps.has(id))
            return !ownTurn && !ownDraw && !ownDrawOnChain && s.phase <= MAIN_ONE
                ? [id === trap.maxx ? 87 : 86, 'opponent-draw-pressure'] : [0];
        if (droll.has(id)) {
            if (!ownTurn) return !ownDraw && !ownDrawOnChain ? [85, 'lock-after-search'] : [0];
            return option === guided && policy.drawPressure.size > 0 && s.draws > 0 ? [85, 'stop-under-draw-trap'] : [0];
        }
        if (id === trap.nibiru) {
            // Its legal prompt already requires at least five opposing summons.
            const ourInteraction = policy.field.some(c => active(c) && protectedBodies.has(code(c)));
            return !ownTurn && isMain(s.phase) && (!ourInteraction || foeFaceUp >= 2) ? [84, 'nibiru-window'] : [0];
        }
        if (id === card.outlaw && desc === OUTLAW_NEGATE)
            return enemyEffect ? [83, 'outlaw-negate'] : [0];
        if (ash.has(id))
            return enemyEffect && (!ownTurn || enemyEffect.code === trap.maxx) ?
                [enemyEffect.code === trap.maxx ? 93 : 76, 'ash-deck-effect'] : [0];
        if (id === trap.veiler || id === trap.imperm) {
            const fromSet = id === trap.imperm && option.chainLocation === SZONE;
            return sourceOnField ? [fromSet ? 82 : id === trap.veiler ? 78 : 77, 'field-negate'] : [0];
        }
        if (id === card.dragon && desc === DRAGON_NEGATE)
            return monsterEffect ? [freeDragonCost ? 81 : urgent ? 80 : 66, 'dragon-negate'] : [0];
        if (id === card.savage && desc === SAVAGE_NEGATE)
            return enemyEffect ? [74, 'savage-negate'] : [0];
        if (id === card.littleKnight && desc === SP_REMOVE)
            return enemyEffect && (policy.threatenedOwn.size > 0 || (enemyFieldEffect && foeFaceUp > 1))
                ? [policy.threatenedOwn.size ? 84 : 69, 'little-knight-temporary-removal'] : [0];
        return null;
    }

    const available = [];
    let preferred, best = 0;
    for (const option of options) {
        const rule = policyFor(option);
        if (rule && !rule[0]) continue;
        available.push(option);
        if (rule?.[0] > best) {
            [best] = rule;
            preferred = { option, reason: rule[1] };
        }
    }
    return { options: available, preferred };
}

function finishSet(policy, options) {
    const set = options.filter(isEndSet);
    const haveField = policy.field.some(c => code(c)) || policy.spells.some(c => code(c));
    for (const id of endSetOrder) {
        if (policy.spells.some(c => code(c) === id && !faceUp(c))) continue;
        if (id === trap.imperm && !haveField) continue;
        if (id === card.descent && !canTurnIntoDragon(policy)) continue;
        const choice = set.find(option => option.card === id);
        if (choice) return choice;
    }
    return undefined;
}

function selectionRules(policy, name, options) {
    const { intent, state: s } = policy;
    if (!intent) return undefined;
    if (name === 'SelectOption' && intent.reason === 'tears-reactive')
        return options.find(option => Number(option.key) === card.tears * 16 + 2);
    if (name === 'SelectYesNo' && intent.reason === 'draw-phase-dragon')
        return options.find(option => option.key === `0:${card.descent * 16 + 2}:true`);
    if (!['SelectCard', 'SelectUnselectCard'].includes(name)) return undefined;
    const kinds = new Set(['moon-droll', 'draw-phase-dragon', 'field-negate',
        'dragon-negate', 'little-knight-temporary-removal']);
    if (!kinds.has(intent.reason)) return undefined;
    const rate = ({ code: id, controller, location, sequence }) => {
        const foe = !policy.own(controller);
        const activatingMonster = foe && location === MZONE && id === intent.target?.code &&
            sequence === intent.target?.sequence;
        switch (intent.reason) {
        case 'moon-droll':
            return !foe && location === SZONE && droll.has(id) ? 100 : 0;
        case 'draw-phase-dragon':
            if (foe) return 0;
            if (location === EXTRA && id === card.dragon) return 120;
            if (fusion.has(id)) return id === card.dark ? 100 : location === SZONE ? 90 : 80;
            return 0;
        case 'field-negate':
            return activatingMonster ? 100 : 0;
        case 'dragon-negate':
            if (s.selectionHint !== 504) return foe ? 100 : 0;
            if (!foe && id !== card.dragon) return location === SZONE ? 100 : protectedBodies.has(id) ? 20 : 60;
            return 0;
        case 'little-knight-temporary-removal':
            if (foe) return activatingMonster ? 120 : 100;
            if (policy.threatenedOwn.has(sequence)) return 80;
            return id === card.littleKnight ? 60 : protectedBodies.has(id) ? 0 : 30;
        }
        return 0;
    };
    let choice, best = 0;
    for (const option of options) {
        const value = (option.selectedCards || []).reduce((sum, card) => sum + rate(card), 0);
        if (value > best) { choice = option; best = value; }
    }
    return choice;
}

module.exports = { chainRules, finishSet, isEndSet, selectionRules };
