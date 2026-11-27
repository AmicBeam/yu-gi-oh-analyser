'use strict';

// Existing trace formats keep damage at index 2 and the deck's end-board
// score at index 3. Primary outcomes are compared before the deck tie score.
function turnAxis(a, b, opening = false) {
    if (a[0] === 1 && b[0] === 1) return -1;
    const indexes = opening ? [0, 1, 2, 3, 4, 5] :
        a.length >= 6 && b.length >= 6 ? [0, 5, 2, 4] : [0, 1, 3, 2, 4];
    return indexes.find(axis => (a[axis] ?? 0) !== (b[axis] ?? 0)) ?? -1;
}

function compareTurn(a, b, opening = false) {
    const axis = turnAxis(a, b, opening);
    return axis < 0 ? 0 : (a[axis] ?? 0) - (b[axis] ?? 0);
}

function tieScore(evidence, weights = {}) {
    const caps = { resources: 4, conversion: 3, timing: 2, resilience: 2 };
    const details = {};
    for (const axis of ['resources', 'conversion', 'timing', 'resilience']) {
        const groups = new Map();
        for (const entry of evidence[axis] ?? []) {
            const current = groups.get(entry.resource);
            if (!current || entry.value > current.value) groups.set(entry.resource, entry);
        }
        const reasons = [...groups.values()].slice(0, caps[axis]);
        const units = Math.min(caps[axis], reasons.reduce((sum, e) => sum + e.value, 0));
        details[axis] = { units, score: units * (weights[axis] ?? 1), reasons };
    }
    return { score: Object.values(details).reduce((sum, axis) => sum + axis.score, 0), details };
}

function verifyTiedRoutes(routes, replay, limit = 8) {
    const groups = new Map();
    for (const route of routes) {
        const [win, clears, damage, board, , total] = route.outcome;
        if (win !== 0 || !route.tiebreak) continue;
        const key = JSON.stringify(total === undefined ? [clears, board, damage] : [total, damage]);
        const group = groups.get(key) ?? [];
        group.push(route);
        groups.set(key, group);
    }
    let remaining = limit;
    for (const group of groups.values()) {
        if (new Set(group.map(route => route.tiebreak.score)).size < 2) continue;
        const ranked = [...group].sort((a, b) => b.tiebreak.score - a.tiebreak.score);
        for (const route of [ranked[0], ranked.at(-1)]) {
            if (remaining-- <= 0) return;
            const result = replay(route);
            route.tieVerified = Boolean(result?.outcome && result.outcome.length === route.outcome.length &&
                result.outcome.every((unit, i) => unit === route.outcome[i]) &&
                result.wire.join('|') === route.wire.join('|'));
        }
    }
}

module.exports = { compareTurn, turnAxis, tieScore, verifyTiedRoutes };
