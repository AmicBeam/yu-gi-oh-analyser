'use strict';

const DRAW_COST_TENTHS = 5;
const PROMOTION_RULE = 'adjusted-total-draw-opening-v1';
const DRAW_HAND_TRAPS = new Set([23434538, 42141493, 84192580]);

function adjusted(result) {
    return result.score - DRAW_COST_TENTHS * result.draws;
}

function totals(rows, side) {
    return rows.reduce((sum, row) => {
        const result = row[side].result;
        if (!result) return { ...sum, missing: sum.missing + 1 };
        return {
            score: sum.score + result.score,
            draws: sum.draws + result.draws,
            reward: sum.reward + adjusted(result),
            missing: sum.missing,
        };
    }, { score: 0, draws: 0, reward: 0, missing: 0 });
}

function judge(suite, evidence) {
    const openings = new Map(suite.map(scene => [scene.id, scene.input.opponent.main.slice(0, 5)]));
    if (openings.size !== suite.length || evidence.length !== suite.length ||
        new Set(evidence.map(row => row.id)).size !== evidence.length ||
        evidence.some(row => !openings.has(row.id)))
        throw new Error('Evaluation evidence does not match the frozen openings');

    const before = totals(evidence, 'before');
    const after = totals(evidence, 'after');
    const drawTrapRows = evidence.filter(row => openings.get(row.id).some(id => DRAW_HAND_TRAPS.has(id)));
    const draw_trap = {
        cases: drawTrapRows.length,
        before: totals(drawTrapRows, 'before'),
        after: totals(drawTrapRows, 'after'),
    };
    const noNewFailures = evidence.every(row => !row.before.result || row.after.result);
    const promote = noNewFailures && after.reward > before.reward &&
        draw_trap.after.reward >= draw_trap.before.reward;
    return { rule: PROMOTION_RULE, before, after, draw_trap, noNewFailures, promote, evidence };
}

module.exports = { DRAW_COST_TENTHS, PROMOTION_RULE, adjusted, judge };
