'use strict';

const P = require('ygopro-msg-encode');
const { card } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');

const { IdleCmdType: I } = P;
const BLACK_CHAOS = 44001993;
const FOUR_CHARMS = 27519978;

function pick(msg, codes, location) {
    const cards = codes.map(code => msg.cards.find(c => c.code === code && (location === undefined || c.location === location)));
    if (cards.every(Boolean)) return msg.prepareResponse(cards.map(c => P.IndexResponse(msg.cards.indexOf(c))));
}

class OutlawPlan {
    reset() {
        this.active = false;
    }

    choose(msg, name, policy) {
        const s = policy.state;
        if (s.phase !== 4 || !policy.own(policy.turnActor)) return;
        const onField = code => policy.field.some(c => c?.code === code && !(c.status & 1));
        const inHand = code => policy.hand.includes(code);
        const hasEnemy = code => policy.enemyField.some(c => c?.code === code);
        const source = s.solving?.code ?? [...s.chains.values()].at(-1)?.code;

        if (name === 'SelectIdleCmd') {
            const normal = msg.summonableCards.find(c => c.code === card.outlaw);
            if (normal && !policy.field.some(c => c?.code) && hasEnemy(BLACK_CHAOS) && hasEnemy(FOUR_CHARMS)) {
                this.active = true;
                return msg.prepareResponse(I.SUMMON, normal);
            }
            if (!this.active) return;
            const activate = (code, location, effect) => {
                const choice = msg.activatableCards.find(c => c.code === code && c.location === location &&
                    (effect === undefined || c.desc === code * 16 + effect));
                return choice && msg.prepareResponse(I.ACTIVATE, choice);
            };
            if (onField(card.savage) && onField(card.outlaw) && msg.canEp)
                return msg.prepareResponse(I.TO_EP);
            if (onField(card.sorcerer) && policy.field.some(c => c?.code === card.verre)) {
                const synchro = msg.spSummonableCards.find(c => c.code === card.savage);
                if (synchro) return msg.prepareResponse(I.SPSUMMON, synchro);
            }
            const setup = onField(card.sorcerer) && inHand(card.verre) && activate(card.sorcerer, 4, 0) ||
                inHand(card.sorcerer) && activate(card.nation, 2) ||
                !inHand(card.sorcerer) && activate(card.king, 8, 1) ||
                !inHand(card.sorcerer) && activate(card.king, 2);
            if (setup) return setup;
            // A new obstruction requires the general rules to reconsider the turn.
            this.active = false;
            return;
        }
        if (!this.active) return;
        if (name === 'SelectTribute' && msg.min === 2 &&
            msg.cards.filter(c => !policy.own(c.controller)).length >= 2)
            return pick(msg, [BLACK_CHAOS, FOUR_CHARMS], 4);

        if (name === 'SelectCard') {
            if (source === card.king) return pick(msg, [card.sorcerer], 1);
            if (source === card.nation) return pick(msg, [card.verre], 1) ?? pick(msg, [card.sorcerer], 2);
            if (source === card.sorcerer) return pick(msg, [card.sorcerer], 4) ??
                pick(msg, [card.maiden], 64) ?? pick(msg, [card.verre], 2);
            if (source === card.savage) return pick(msg, [card.maiden], 16);
        }
        if (name === 'SelectYesNo' && source === card.nation) return msg.prepareResponse(true);
        if (name === 'SelectEffectYn') {
            if ([card.sorcerer, card.savage].includes(msg.code)) return msg.prepareResponse(true);
            if (msg.code === card.verre) return msg.prepareResponse(false);
        }
        if (name === 'SelectSum' && msg.sumVal === 8 && msg.mustSelectCards.some(c => c.code === card.sorcerer)) {
            const index = msg.cards.findIndex(c => c.code === card.verre && (c.opParam & 0xffff) === 4);
            if (index >= 0) return msg.prepareResponse([P.IndexResponse(index)]);
        }
        if (name === 'SelectPlace' && msg.count <= 1) {
            const place = msg.getSelectablePlaces().find(p => policy.own(p.player));
            if (place) return msg.prepareResponse([place]);
        }
    }
}

module.exports = { OutlawPlan };
