'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const P = require('ygopro-msg-encode');
const { card, trap } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');
const { chainRules, finishSet, selectionRules } = require('./live-rules.cjs');
const { MagistusPolicy } = require('./live-policy.cjs');
const { features: secondFeatures } = require('./second-turn.cjs');
const { planCombat } = require('./public-combat.cjs');

const pass = { key: 'pass' };
const effect = (id, index = 0, location = 4) => ({ key: `${id}:${id * 16 + index}`, chainLocation: location });
const monster = code => ({ code, position: 1 });
const select = (code, location, controller = 1, sequence = 0) => ({
    selectedCards: [{ code, location, controller, sequence }],
});

function fixture(phase, target, location = 4) {
    return {
        seat: 1, turnActor: 0, own: player => player === 1, field: [], spells: [],
        enemyField: target && location === 4 ? [monster(target)] : [],
        extra: [card.dragon], ownDrawPressure: new Set(), threatenedOwn: new Set(),
        state: { phase, cardReader: () => ({ type: 1 }), chains: new Map(target ?
            [[1, { code: target, location, player: 1, sequence: 0 }]] : []) },
    };
}

test('draw-phase Descent uses spare Fusion material and chooses Dragon', () => {
    const policy = fixture(1);
    const spell = effect(card.descent, 0, 8);
    const rule = () => chainRules(policy, [pass, spell]);
    assert.deepEqual(rule().options, [pass]);
    policy.spells.push(monster(card.dark));
    assert.equal(rule().preferred.option, spell);
    policy.intent = { reason: rule().preferred.reason };
    const board = select(card.corozo, 4), equip = select(card.dark, 8);
    const summon = select(card.dragon, 64);
    assert.equal(selectionRules(policy, 'SelectCard', [board, equip]), equip);
    assert.equal(selectionRules(policy, 'SelectCard', [board, summon]), summon);
    policy.state.phase = 4;
    assert.deepEqual(rule().options, [pass]);
});

test('Moon returns own equipped Droll, and draw effects do not stack', () => {
    const policy = fixture(4);
    const moon = effect(card.moon, 1);
    const maxx = effect(trap.maxx), fuwalos = effect(trap.fuwalos);
    assert.equal(chainRules(policy, [pass, fuwalos, maxx]).preferred.option, maxx);
    policy.spells.push({ ...monster(card.droll), owner: 1 });
    assert.equal(chainRules(policy, [pass, moon]).preferred.option, moon);
    const droll = select(card.droll, 8);
    policy.intent = { reason: 'moon-droll' };
    assert.equal(selectionRules(policy, 'SelectCard', [select(card.king, 8), droll]), droll);
    policy.state.chains.set(1, { code: trap.maxx, location: 2, player: 0 });
    assert.deepEqual(chainRules(policy, [pass, fuwalos, moon]).options, [pass]);
    policy.state.chains.clear();
    policy.ownDrawPressure.add(trap.maxx);
    assert.deepEqual(chainRules(policy, [pass, fuwalos, moon, effect(card.droll)]).options, [pass]);
});

test('field negate uses set Imperm first and does not waste it on a departed source', () => {
    const policy = fixture(4, card.spoon);
    const setImperm = effect(trap.imperm, 0, 8), handImperm = effect(trap.imperm, 0, 2);
    const veiler = effect(trap.veiler);
    assert.equal(chainRules(policy, [pass, veiler, handImperm, setImperm]).preferred.option, setImperm);
    policy.intent = { reason: 'field-negate', target: policy.state.chains.get(1) };
    const duplicate = select(card.spoon, 4, 0, 1), source = select(card.spoon, 4, 0, 0);
    assert.equal(selectionRules(policy, 'SelectCard', [duplicate, source]), source);
    policy.enemyField = [monster(card.cendrillon), monster(card.spoon)];
    assert.deepEqual(chainRules(policy, [pass, veiler, handImperm, setImperm]).options, [pass]);
});

test('Dragon can stop Nibiru and prioritizes the equipped payment', () => {
    const policy = fixture(4, trap.nibiru, 2);
    policy.turnActor = 1;
    policy.spells.push(monster(card.maiden));
    const savage = effect(card.savage, 1), dragon = effect(card.dragon, 1);
    const rule = chainRules(policy, [pass, savage, dragon]).preferred;
    assert.equal(rule.option, dragon);
    policy.intent = { reason: rule.reason };
    policy.state.selectionHint = 504;
    const self = select(card.dragon, 4), equip = select(card.maiden, 8);
    assert.equal(selectionRules(policy, 'SelectCard', [self, equip]), equip);
});

test('a Glass summoned by Sorcerer declines its trigger before the status update', () => {
    const policy = new MagistusPolicy(1, () => null, [], null, () => {});
    const move = (from, to) => new P.YGOProMsgMove().fromPartial({ code: card.verre,
        previous: { controller: 1, location: from, sequence: 0 },
        current: { controller: 1, location: to, sequence: 0, position: 4 },
    });
    const prompt = new P.YGOProMsgSelectEffectYn().fromPartial({
        player: 1, code: card.verre, controller: 1, location: 4, sequence: 0, desc: 221,
    });
    policy.observe(new P.YGOProMsgChaining().fromPartial({
        chainCount: 1, controller: 1, code: card.sorcerer, desc: card.sorcerer * 16 + 1,
    }));
    policy.observe(new P.YGOProMsgChainSolving().fromPartial({ chainCount: 1 }));
    policy.observe(move(16, 4));
    policy.observe(new P.YGOProMsgChainEnd());
    assert.equal(policy.field[0].status, undefined);
    assert.deepEqual(policy.choose(prompt), prompt.prepareResponse(false));

    const trigger = new P.YGOProMsgSelectChain().fromPartial({
        player: 1, chains: [{ code: card.verre, controller: 1, location: 4, sequence: 0, desc: card.verre * 16 + 1 }],
    });
    assert.deepEqual(policy.choose(trigger), trigger.prepareResponse());

    policy.observe(move(4, 16));
    policy.observe(move(2, 4));
    assert.deepEqual(policy.choose(prompt), prompt.prepareResponse(true));
});

test('Tears chooses summon when chaining to the opponent, and Little Knight protects targeted bodies', () => {
    const policy = fixture(4, card.spoon);
    const tears = effect(card.tears, 0, 8), knight = effect(card.littleKnight, 1);
    const rule = chainRules(policy, [pass, tears, knight]).preferred;
    assert.equal(rule.option, tears);
    policy.intent = { reason: rule.reason };
    const dump = { key: String(card.tears * 16 + 1) }, summon = { key: String(card.tears * 16 + 2) };
    assert.equal(selectionRules(policy, 'SelectOption', [dump, summon]), summon);
    policy.tearsReactiveUsed = true;
    assert.deepEqual(chainRules(policy, [pass, tears]).options, [pass]);
    policy.tearsReactiveUsed = false;
    policy.threatenedOwn.add(0);
    assert.equal(chainRules(policy, [pass, tears, knight]).preferred.option, knight);
});

test('end set keeps the empty-board Imperm and sets at most one per name', () => {
    const policy = fixture(4);
    const set = card => ({ key: `${P.IdleCmdType.SSET}/${card}:0:2/0`, card });
    const imperm = set(trap.imperm), descent = set(card.descent), tears = set(card.tears);
    const offers = [tears, descent, imperm];
    assert.equal(finishSet(policy, offers), tears);
    policy.field.push(monster(card.corozo));
    assert.equal(finishSet(policy, offers), imperm);
    policy.spells.push({ code: trap.imperm, position: 8 });
    assert.equal(finishSet(policy, offers), descent);
    policy.spells.push({ code: card.descent, position: 8 }, { code: card.tears, position: 8 });
    assert.equal(finishSet(policy, offers), undefined);
});

test('an immediately lethal direct attack stays ahead of a learned Main 2 preference', () => {
    const model = { weights: { 'action:SelectBattleCmd:main2': 100 } };
    const policy = new MagistusPolicy(1, () => ({ attack: 2500 }), [], null, () => {}, [], model);
    policy.lp[0] = 2400;
    policy.initialOwnTurn = policy.state.turn = 2;
    const battle = new P.YGOProMsgSelectBattleCmd().fromPartial({
        player: 1, canM2: 1, attackableCount: 1, activatableCount: 0, activatableCards: [],
        attackableCards: [{ code: card.savage, controller: 1, location: 4, sequence: 2, directAttack: 1 }],
    });
    assert.deepEqual(policy.choose(battle), battle.prepareResponse(P.BattleCmdType.ATTACK, P.IndexResponse(0)));
});

test('second-turn target features match across opposing monsters with the same public stats', () => {
    const policy = new MagistusPolicy(1, () => null, [], null, () => {});
    policy.enemyMonsters = [true];
    const featuresFor = code => {
        policy.enemyField = [{ code, position: 1, attack: 2500 }];
        const option = { key: `${code}:0:4`, selectedCards: [{ code, controller: 0, location: 4, sequence: 0 }] };
        return secondFeatures(policy, 'SelectCard', option);
    };
    assert.deepEqual(featuresFor(46986414), featuresFor(60461804));
});

test('continuation weights affect Main 1 and leave battle and Main 2 on rule ranking', () => {
    const model = { training: 'rule-continuation', weights: { 'action:SelectIdleCmd:learned': 3 } };
    const policy = new MagistusPolicy(1, () => null, [], null, () => {}, [], model);
    policy.initialOwnTurn = policy.state.turn = 2;
    const choices = [{ key: 'rule', weight: 2 }, { key: 'learned', weight: 1 }];
    policy.state.phase = 4;
    assert.equal(policy.rank('SelectIdleCmd', choices, o => o.weight).key, 'learned');
    assert.equal(policy.rank('SelectBattleCmd', choices, o => o.weight).key, 'rule');
    policy.state.phase = 256;
    assert.equal(policy.rank('SelectIdleCmd', choices, o => o.weight).key, 'rule');
});

test('combat clears with the smaller attacker when the stronger attacker can then win', () => {
    const attackers = [{ sequence: 0, attack: 3000 }, { sequence: 1, attack: 2000 }];
    const defenders = [{ sequence: 4, strength: 1900, defending: true }];
    assert.deepEqual(planCombat(attackers, defenders, 3000, true),
        { win: true, clears: 1, damage: 3000, attacker: 1, target: 4 });
    assert.equal(planCombat(attackers, defenders, 3000, false).win, false);
});

test('a one-change continuation model falls back to rule ranking after its first override', () => {
    const model = { training: 'rule-continuation', overrideLimit: 1, weights: { 'action:SelectIdleCmd:change': 3 } };
    const policy = new MagistusPolicy(1, () => null, [], null, () => {}, [], model);
    policy.initialOwnTurn = policy.state.turn = 2;
    policy.state.phase = 4;
    const choices = [{ key: 'rule', weight: 2 }, { key: 'change', weight: 1 }];
    assert.equal(policy.rank('SelectIdleCmd', choices, o => o.weight).key, 'change');
    assert.equal(policy.rank('SelectIdleCmd', choices, o => o.weight).key, 'rule');
});
