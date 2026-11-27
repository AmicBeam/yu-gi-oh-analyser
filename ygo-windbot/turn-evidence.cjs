'use strict';

const { tieScore } = require('./turn-objective.cjs');

const point = (resource, reason) => ({ resource, value: 1, reason });

function retainedHand(hand, windows, emptyField = false) {
    return [...new Set(hand)].flatMap(code => {
        const label = windows.get(code);
        return label && (label !== 'empty-field' || emptyField) ?
            [point('hand:' + code, 'retained card ' + code + ' for its ' + label + ' window')] : [];
    });
}

function amazonessTie(C, view, state, board) {
    const have = id => view.hand.includes(id);
    const body = view.own.filter(c => c.location === 4 && c.position & 5);
    const retained = retainedHand(view.hand, new Map([
        [C.ash, 'deck interaction'], [C.belle, 'grave interaction'], [C.ogre, 'field interaction'],
        [C.veiler, 'opponent Main Phase'], [C.imperm, 'empty-field'], [C.maxx, 'special-summon'],
    ]), view.own.length === 0);
    if (have(C.fighter) || have(C.curse))
        retained.push(point('normal', 'one independent normal summon for the next own turn'));
    const conversion = [];
    if (view.grave.includes(C.rescue) && view.grave.some(id => [C.fighter, C.harpie].includes(id)) &&
        !have(C.fighter) && !have(C.harpie))
        conversion.push(point('rescue-resource', 'grave Rescue can recover a playable Fighter or Harpie'));
    if (board.sword && body.filter(c => c.code !== C.sword).length > 1)
        conversion.push(point('spare-body', 'Sword keeps a spare body after reserving one for its release cost'));
    const timing = (state.realizedResources ?? []).map(code =>
        point('resource:' + code, 'the engine resolved summon ' + code + ' from Rescue or Formation first'));
    const resilience = (state.deniedWindows ?? []).slice(0, 1).map(code =>
        point('cover', 'effect ' + code + ' was stopped at a legal response window'));
    if (state.hit && retained.some(e => e.resource === 'normal'))
        resilience.push(point('next-normal', 'an independent normal-summon starter remains after the opposing effect'));
    return tieScore({ resources: retained, conversion, timing, resilience },
        { resources: 5, conversion: 3, timing: 2, resilience: 2 });
}

function magistusTie(card, trap, policy, endBoard) {
    const hand = policy.hand.filter(Boolean);
    const resources = retainedHand(hand, new Map([
        [trap.ash, 'deck interaction'], [trap.veiler, 'opponent Main Phase'], [trap.belle, 'grave interaction'],
        [trap.ogre, 'field interaction'], [trap.imperm, 'empty-field'], [trap.maxx, 'special-summon'],
    ]), !policy.field.some(c => c?.code) && !policy.spells.some(c => c?.code));
    if (hand.includes(card.spoon)) resources.push(point('spoon', 'Spoon is retained as an independent hand search'));
    if (hand.includes(card.verre) && hand.some(id => [card.nation, card.mastery, card.tears].includes(id)))
        resources.push(point('glass-plus-spell', 'Glass has a spell in hand to make its next summon accessible'));
    const conversion = [];
    if (endBoard.dragon && endBoard.droll_access)
        conversion.push(point('recover-equip', 'Moon can recover the live Droll while Dragon is established'));
    const timing = policy.state.events.filter(e => e[0] === 'resource' && e[2] === 0).map(e =>
        point('setup:' + e[3], 'the engine settled a resource effect from ' + e[3]));
    const resilience = policy.state.events.filter(e => e[0] === 'coverage').slice(0, 1).map(e =>
        point('cover', 'the engine confirmed protection at effect window ' + e[3]));
    if (policy.secondInterrupted && resources.some(r => r.resource === 'spoon'))
        resilience.push(point('spoon-recovery', 'a resolved opposing effect left the independent Spoon search available'));
    return tieScore({ resources, conversion, timing, resilience },
        { resources: 5, conversion: 2, timing: 1, resilience: 3 });
}

function sunnyEvidence(position) {
    const sunny = 93672138;
    const pair = [9205573, 36609518].every(id => position.grave.includes(id) && !position.unrevivable.includes(id));
    const freeMain = position.board.slice(0, 5).some(id => !id);
    const live = !position.board.includes(65330383);
    if (pair && freeMain && live && (position.board.includes(sunny) ||
        position.extra.includes(sunny) && position.board.includes(25154) &&
        position.grave.some(id => [6636319, 25152, 25153, 98360333, 25156].includes(id))))
        return point('sunny', 'Sunny can split into the available red and blue');
    if (freeMain && position.grave.includes(sunny) && position.grave.includes(60764609) &&
        !position.grave.some(id => [6636319, 2463794, 49867899, 9205573, 82135803, 46640168, 65330383].includes(id)))
        return point('sunny', 'Sunny supplies the missing LIGHT Fiend for Engraver and can return to Extra Deck');
    return undefined;
}

function twinsTie(position, witness = {}) {
    const resources = retainedHand(position.hand, new Map([
        [14558127, 'deck interaction'], [14558128, 'deck interaction'], [59438930, 'field interaction'],
        [10045474, 'empty-field'], [97268402, 'opponent Main Phase'], [23434538, 'special-summon'],
    ]), !position.board.some(Boolean) && !position.set.length);
    if (position.hand.some(id => [36326160, 73810864, 82699999, 25150].includes(id)))
        resources.push(point('next-normal', 'one Live Twin can independently supply the next normal summon'));
    if (position.hand.includes(25151) && position.grave.some(id => [36326160, 73810864, 82699999, 25150].includes(id)))
        resources.push(point('photo', 'Night Photo has a legal Twin family in the grave to recover'));
    const conversion = [];
    if (position.set.includes(98360333) && position.grave.includes(36609518) && !position.unrevivable.includes(36609518))
        conversion.push(point('blue-challenge', 'Challenge retains a properly summoned Big Blue as a revival target'));
    const sunny = sunnyEvidence(position);
    if (sunny) conversion.push(sunny);
    const timing = position.early_requiem && position.activations.some(e => e[0] === 2463794 && e[2] === 39420704) ?
        [point('requiem', 'the verified route opened Requiem by the fifth summon and used its effect')] : [];
    const resilience = witness.protectedBeforeWindow ?
        [point('protection', 'protection was present at the observed summon window')] : [];
    if (witness.hardHit && resources.some(r => r.resource === 'next-normal'))
        resilience.push(point('next-normal', 'the checked interference left an independent starter'));
    return tieScore({ resources, conversion, timing, resilience },
        { resources: 5, conversion: 2, timing: 2, resilience: 3 });
}

module.exports = { amazonessTie, magistusTie, twinsTie };
