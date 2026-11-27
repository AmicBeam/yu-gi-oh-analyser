'use strict';

const P = require('ygopro-msg-encode');
const { belle, sunnyRevival } = require('./twins-interference.cjs');

const crow = 24508238, disaster = 30316053, griffoh = 97462632, sword = 77456448, ritual = 33599853;
const dragon = 70088809, master = 72444406, requiem = 2463794;
const veiler = 97268402, queen = 65302903, purge = 97045737;
const byl = [33854624, 6637331, 60242223, 72656408];
const dominus = [58053438, 40366667, 97045737, 42091632, 6325660];
const drawTraps = [23434538, 42141493, 84192580, 87126721, 47605577];
const staples = [27204311, 14558128, 94145022, 59438930, 10045474, belle, veiler, queen];
const pressureCards = new Set([crow, disaster, griffoh, ...byl, ...dominus, dragon, ...drawTraps, ...staples]);
const negations = new Set([58053438, 40366667, purge, 14558128, 10045474, belle, sword, veiler]);
const fieldTargets = [6636319, 4731783, 49867899, 25152, 25154, 93672138, 9205573, 36609518, 9205574,
    36609519, 36326160, 73810864, 82699999, 25150];
const graveTargets = [9205573, 36609518, 9205574, 36609519, 60764609, 49867899, 2463794, 24269961,
    25152, 25150, 73810864, 36326160, 93672138];
const spells = [37582948, 25151, 2463794, 49867899, 61976639];

class MixedInterference {
    constructor(trace, monsters, enabled) {
        this.trace = trace;
        this.monsters = monsters;
        this.enabled = enabled;
        this.grave = [];
        this.chains = new Map();
        this.counters = new Map();
    }

    observe(event) {
        if (event[0] === 'Move' && event[2] === 16 && event[5] === 0) {
            const index = this.grave.indexOf(event[1]);
            if (index >= 0) this.grave.splice(index, 1);
        }
        if (event[0] === 'Move' && event[3] === 16 && event[7] === 0)
            this.grave.push(event[1]);
        if (event[0] === 'Chaining')
            this.chains.set(event[5], event);
        if (event[0] === 'ChainSolving')
            this.solving = this.chains.get(event[1])?.[1];
        if (event[0] === 'ChainSolved' || event[0] === 'ChainEnd')
            this.solving = undefined;
        if (event[0] === 'ChainEnd') {
            this.chains.clear();
            this.current = undefined;
        }
    }

    choose(game, phase, summons, pending) {
        if (!this.enabled || ![2, 4, 512].includes(phase)) return undefined;
        const prior = [...this.chains.values()].at(-1);
        const active = prior?.[2] === 0 ? prior : null;
        const source = active?.[1];
        let best, priority = 0;
        for (const choice of game.chains) {
            const code = choice.code;
            const count = this.counters.get(code) || 0;
            if (count && !drawTraps.includes(code)) continue;
            let value = 0;
            if (drawTraps.includes(code) && phase !== 512 && this.chains.size === 0)
                value = 140 - drawTraps.indexOf(code);
            else if (code === 27204311 && summons === 5)
                value = 128;
            else if (code === disaster && pending && choice.location === 2)
                value = 125;
            else if (code === belle && sunnyRevival(active))
                value = 120;
            else if (code === griffoh && !this.chains.size && phase === 4)
                value = 115;
            else if (code === queen && !this.chains.size && phase !== 512)
                value = 112;
            else if (code === sword && this.trace.some(e => e[0] === 'Move'
                && e[1] === sword && e[2] === 1 && e[3] === 8 && e[7] === 1))
                value = source && (fieldTargets.includes(source) || spells.includes(source)) ? 108 : 82;
            else if (code === 94145022)
                value = 114;
            else if (code === dragon && active?.[3] === 4 && [6636319, 25152, 2463794, 49867899, 4731783].includes(source))
                value = 110;
            else if (code === master && this.counters.has(dragon))
                value = 110;
            else if ([crow, ...byl].includes(code) && this.grave.some(card => graveTargets.includes(card)))
                value = active?.[3] === 16 || [9205573, 36609518, 9205574, 36609519, 4731783].includes(source) ? 105 : 62;
            else if (negations.has(code) && code !== belle && code !== sword && (fieldTargets.includes(source) || source === requiem
                || (code === 97045737 && spells.includes(source))))
                value = 100;
            else if ([42091632, 6325660].includes(code) && [...this.monsters.keys()].some(key => key < 8))
                value = active?.[3] === 4 ? 95 : 71;
            else if (code === 59438930 && active?.[3] === 4 && [6636319, 4731783, 49867899, 25152].includes(source))
                value = 94;
            if (value > priority) {
                best = choice;
                priority = value;
            }
        }
        if (best) {
            this.current = best.code;
            this.counters.set(best.code, (this.counters.get(best.code) || 0) + 1);
            this.trace.push(['MixedPick', best.code, source || 0, active?.[5] || 0, summons, phase]);
        }
        return best;
    }

    cardWeight(card) {
        const source = this.solving || this.current;
        const code = card.code || this.monsters.get(card.controller * 8 + card.sequence) || 0;
        if (source === dragon && card.location === 64) return code === master ? 100 : 50;
        if (source === griffoh) return code === sword ? 100 : 0;
        if (source === queen && card.location === 1) return code === purge ? 100 : 0;
        if (source === queen && card.location === 2) return code === purge ? -100 : 0;
        if (source === sword && code === ritual) return 100;
        if (source === 58053438 && dominus.includes(code)) return 60;
        const preferences = card.location === 16 ? graveTargets : fieldTargets;
        const index = preferences.indexOf(code);
        const chainSource = [...this.chains.values()].findLast(event => event[2] === 0);
        const value = index < 0 ? 0 : preferences.length - index;
        return (card.controller === 0 ? 40 : 0) + (card.controller === 0 && code === chainSource?.[1] ? 24 : 0) + value;
    }

    chooseCards(game, unselect) {
        const cards = unselect ? game.selectableCards : game.cards;
        const source = this.solving || this.current;
        const ordered = [...cards].sort((a, b) => this.cardWeight(b) - this.cardWeight(a));
        if (unselect) {
            const done = game.finishable && (source !== dragon || game.unselectableCards.length >= 5);
            if (done || (!ordered.length && game.cancelable)) return game.prepareResponse(null);
            return game.prepareResponse(ordered[0]);
        }
        const want = source === dragon && cards.some(c => c.location === 64) ? 5 : game.min || 1;
        const chosen = ordered.slice(0, Math.min(game.max, want));
        for (const card of chosen)
            this.trace.push(['MixedTarget', source || 0, card.code || this.monsters.get(card.controller * 8 + card.sequence) || 0,
                card.location, card.controller, card.sequence]);
        return game.prepareResponse(chosen);
    }

    option(game) {
        const source = this.solving || this.current;
        const preferred = new Map([[griffoh, 2], [47605577, 1], [sword, 2]]);
        const index = game.options.indexOf(source * 16 + preferred.get(source));
        const option = Math.max(index, 0);
        this.trace.push(['MixedOption', source || 0, game.options[option]]);
        return game.prepareResponse(P.IndexResponse(option));
    }
}

module.exports = { MixedInterference, pressureCards, drawTraps };
