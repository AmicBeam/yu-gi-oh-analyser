'use strict';

function compare(a, b) {
    return Number(a.win) - Number(b.win) || a.clears - b.clears || a.damage - b.damage;
}

// Models ordinary battle from public stats. Actual legal targets are checked at
// the selection prompt, and the next attack is always planned from the new field.
function planCombat(attackers, defenders, lp, directAfterClear) {
    const memo = new Map();
    let visited = 0;
    function best(ours, theirs, life) {
        if (life <= 0) return { win: true, clears: 0, damage: 0 };
        const key = `${ours}/${theirs}/${life}`;
        if (memo.has(key)) return memo.get(key);
        if (++visited > 6000) return { win: false, clears: 0, damage: 0 };
        let result = { win: false, clears: 0, damage: 0 };
        for (let i = 0; i < attackers.length; i++) {
            if (!(ours & 1 << i)) continue;
            const a = attackers[i];
            if (a.attack <= 0) continue;
            const remaining = ours ^ 1 << i;
            const trial = (target, cleared, damage, enemies) => {
                const future = best(remaining, enemies, life - damage);
                const next = { win: future.win, clears: cleared + future.clears, damage: damage + future.damage,
                    attacker: a.sequence, target };
                if (compare(next, result) > 0) result = next;
            };
            if (a.direct || (!theirs && directAfterClear)) trial(undefined, 0, a.attack, theirs);
            for (let j = 0; j < defenders.length; j++) {
                if (!(theirs & 1 << j)) continue;
                const d = defenders[j];
                if (a.attack < d.strength || (a.attack === d.strength && d.defending)) continue;
                const damage = d.defending ? 0 : a.attack - d.strength;
                trial(d.sequence, 1, damage, theirs ^ 1 << j);
            }
        }
        memo.set(key, result);
        return result;
    }
    return best((1 << attackers.length) - 1, (1 << defenders.length) - 1, lp);
}

module.exports = { planCombat };
