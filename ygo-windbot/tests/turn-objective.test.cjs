'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { compareTurn, tieScore, turnAxis, verifyTiedRoutes } = require('../turn-objective.cjs');

test('one pressure score counts removal and retained interaction before damage', () => {
    assert(compareTurn([1, 0, 0, 0, 0, 0], [0, 4, 7999, 4, 0, 8]) > 0);
    assert(compareTurn([0, 1, 0, 3, 0, 4], [0, 2, 5000, 1, 0, 3]) > 0);
    assert(compareTurn([0, 1, 500, 3, 0, 4], [0, 2, 300, 2, 100, 4]) > 0);
    assert.equal(turnAxis([0, 1, 500, 3, 1, 4], [0, 2, 500, 2, 0, 4]), 4);
});

test('shared resources count once within each outcome axis with its strongest evidence', () => {
    const tie = tieScore({
        resources: [{ resource: 'normal', value: 2, reason: 'normal summon available' },
            { resource: 'normal', value: 1, reason: 'same normal as an alternative' }],
        conversion: [{ resource: 'equip', value: 1, reason: 'cost remains payable' }],
        timing: [{ resource: 'window', value: 1, reason: 'effect stopped' }],
        resilience: [{ resource: 'recovery', value: 2, reason: 'continuation resolved' }],
    });
    assert.equal(tie.score, 6);
    assert.equal(tie.details.resources.reasons.length, 1);
    assert.equal(tie.details.resources.reasons[0].reason, 'normal summon available');
});

test('a training tie requires complete legal replay evidence for both routes', () => {
    const retained = { outcome: [0, 1, 1000, 2, 5, 3], wire: ['a', 'b'], tiebreak: { score: 5 } };
    const spent = { outcome: [0, 1, 1000, 2, 0, 3], wire: ['a', 'c'], tiebreak: { score: 0 } };
    verifyTiedRoutes([retained, spent], route => route === retained ? route : { ...route, wire: ['a', 'd'] });
    assert.equal(retained.tieVerified, true);
    assert.equal(spent.tieVerified, false);
    verifyTiedRoutes([retained, spent], route => route);
    assert.equal(spent.tieVerified, true);
    assert(compareTurn(retained.outcome, spent.outcome) > 0);
});
