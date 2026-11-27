'use strict';

const P = require('ygopro-msg-encode');
const { card } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');

const VERSION = 'magistus-second-turn-v2';
const actionable = new Set(['SelectIdleCmd', 'SelectBattleCmd', 'SelectCard', 'SelectUnselectCard',
    'SelectEffectYn', 'SelectChain', 'SelectOption']);
const count = list => list.filter(Boolean).length;
const bucket = (n, size, max) => Math.max(0, Math.min(max, Math.ceil(n / size)));

function features(policy, prompt, option, continuation = false) {
    const { state: s } = policy;
    const key = option.selectedCards?.some(c => !policy.own(c.controller))
        ? option.selectedCards.map(c => {
            if (policy.own(c.controller)) return `own:${c.code}:${c.location}`;
            const target = c.location === 4 ? policy.enemyField[c.sequence] : undefined;
            const strength = target && (target.position === 1 ? target.attack : target.defense);
            return `opponent:${c.location}:${target?.position || 'unknown'}:${bucket(strength || 0, 500, 8)}`;
        }).sort().join(',')
        : option.key;
    const attack = policy.field.reduce((sum, c) =>
        sum + (c?.position === 1 ? Math.max(0, c.attack ?? s.cardReader(c.code)?.attack ?? 0) : 0), 0);
    const exposed = policy.enemyField.filter(Boolean);
    const enemyStrength = exposed.reduce((max, c) => Math.max(max,
        (c.position === 1 ? c.attack : c.defense) ?? 0), 0);
    const keys = [`action:${prompt}:${key}`, `phase:${s.phase}:${key}`,
        `body:${count(policy.field)}:${key}`, `enemy:${count(policy.enemyMonsters)}:${key}`,
        `hidden:${count(policy.enemyMonsters) - exposed.length}:${key}`,
        `backrow:${count(policy.enemyBackrow)}:${key}`, `attack:${bucket(attack, 1000, 8)}:${key}`,
        `enemy-strength:${bucket(enemyStrength, 500, 8)}:${key}`,
        `lp:${bucket(policy.lp[1 - policy.seat], 1000, 8)}:${key}`,
        `hit:${policy.secondInterrupted}:${key}`, `normal:${policy.normalUsed}:${key}`];
    if (continuation) {
        for (const id of new Set(s.hand)) keys.push(`hold:${id}:${key}`);
        for (const id of new Set(s.board)) keys.push(`field:${id}:${key}`);
        keys.push(`resources:${s.hand.length}:${s.normalUsed ?? policy.normalUsed}:${key}`);
    }
    return keys;
}

function battleOptions(msg, policy) {
    const C = P.BattleCmdType;
    const result = msg.attackableCards.map((c, i) => {
        const attack = policy.field[c.sequence]?.attack ?? policy.state.cardReader(c.code)?.attack ?? 0;
        const lethal = c.directAttack && attack >= policy.lp[1 - policy.seat];
        return { key: `attack:${c.code}:${Number(Boolean(c.directAttack))}`,
            lethal, weight: c.directAttack ? 32 + attack / 150 : 18,
            attackSequence: c.sequence,
            response: msg.prepareResponse(C.ATTACK, P.IndexResponse(i)) };
    });
    result.push(...msg.activatableCards.map((c, i) => ({
        key: `battle-effect:${c.code}:${c.desc}`, weight: 10, response: msg.prepareResponse(C.ACTIVATE, P.IndexResponse(i)),
    })));
    if (msg.canM2) result.push({ key: 'main2', weight: 9, response: msg.prepareResponse(C.TO_M2) });
    else if (msg.canEp) result.push({ key: 'end', weight: 1, response: msg.prepareResponse(C.TO_EP) });
    return result;
}

function actionPrior(policy, name, option, prior) {
    const { state: s } = policy;
    if (name === 'SelectIdleCmd' && s.phase === 4) {
        const bodies = count(policy.enemyField);
        if (option.key.startsWith('5/') && bodies && [card.dragon, card.aruru].includes(option.card))
            return Math.max(prior, 67);
        if (option.key.startsWith('1/') && bodies && option.card === card.littleKnight)
            return Math.max(prior, 42);
    }
    if (name !== 'SelectCard' || !option.selectedCards) return prior;
    const enemy = option.selectedCards.filter(c => !policy.own(c.controller));
    if (!enemy.length) return prior;
    if (s.selectionHint === 549) {
        const attacker = policy.field[policy.attackSequence];
        const attack = attacker ? attacker.attack ?? s.cardReader(attacker.code)?.attack ?? 0 : 0;
        const target = policy.enemyField[enemy[0].sequence];
        if (!target) return 4;
        const defense = target.position === 1 ? target.attack : target.defense;
        return attack > defense ? 24 + (target.attack || 0) / 150 : attack === defense && attack > 0 ? 15 : 0.5;
    }
    if ([card.dragon, card.littleKnight, card.aruru].includes(s.chainSource || s.lastIdle) &&
        enemy.some(c => c.location === 4 || c.location === 8)) return Math.max(prior, 36);
    return prior;
}

function learned(model, keys) {
    return Math.max(-4, Math.min(4, keys.reduce((sum, key) => sum + (model?.weights[key] || 0), 0)));
}

module.exports = { VERSION, actionable, features, battleOptions, actionPrior, learned };
