'use strict';

const P = require('ygopro-msg-encode');
const { C, F, L, normal, attackProjection } = require('./duel-policy.cjs');

const { IdleCmdType: I, OcgcoreScriptConstants: S } = P;
const codeIs = (card, code) => normal(card.code) === code;

function cardPick(msg, cards, codes, location) {
    const selected = codes.map(code => cards.find(c => c.location === location && codeIs(c, code)));
    if (selected.some(c => !c)) return;
    return msg.prepareResponse(selected.map(c => P.IndexResponse(cards.indexOf(c))));
}

class MainPhaseLethal {
    reset() {
        this.active = false;
        this.formationTried = false;
        this.swordSummoning = false;
    }

    choose(msg, name, s, view, extra) {
        if (s.phase !== F.main1 || s.turnPlayer !== s.bot) return;
        const own = view.own.filter(c => c.location === L.monster);
        const enemies = view.enemy.filter(c => c.location === L.monster);
        if (enemies.length) {
            this.active = false;
            return;
        }
        const hand = code => view.hand.includes(code);
        const field = code => own.some(c => codeIs(c, code));
        const source = s.solving?.code ?? [...s.chains.values()].at(-1)?.code;
        if (!this.active) {
            if (name !== 'SelectCard' || source !== C.rescue || s.solving || msg.min !== 1 ||
                !field(C.fighter) || !field(C.harpie) || !hand(C.chief) || !hand(C.princess) ||
                !hand(C.formation) || !extra.includes(C.sword) || s.lp[1 - s.bot] > 8300)
                return;
            const response = cardPick(msg, msg.cards, [C.harpie], L.monster);
            if (!response) return;
            this.active = true;
            return response;
        }
        if (name === 'SelectIdleCmd') {
            const activate = (code, location, effect) => {
                const card = msg.activatableCards.find(c => codeIs(c, code) && c.location === location &&
                    (effect === undefined || c.desc === code * 16 + effect));
                return card && msg.prepareResponse(I.ACTIVATE, card);
            };
            if (msg.canBp && attackProjection(own, []) >= s.lp[1 - s.bot])
                return msg.prepareResponse(I.TO_BP);
            const chief = !field(C.chief) && activate(C.chief, L.hand);
            const fusion = field(C.chief) && !field(C.sword) && hand(C.princess) && hand(C.fighter) &&
                activate(C.curse, L.monster, 1);
            if (chief || fusion) return chief || fusion;
            if (field(C.sword) && !this.formationTried) {
                const formation = activate(C.formation, L.hand, 0) || activate(C.formation, L.spell, 0);
                if (formation) this.formationTried = true;
                if (formation) return formation;
            }
            this.active = false;
        }
        if (name === 'SelectEffectYn') {
            if (msg.code === C.chief) return msg.prepareResponse(false);
            if ([C.curse, C.sword].includes(msg.code)) return msg.prepareResponse(true);
        }
        if (name === 'SelectChain' && !msg.chains.some(c => c.forced)) {
            const opposing = [...s.chains.values()].at(-1);
            if (opposing?.player !== undefined && opposing.player !== s.bot && msg.chains.some(c =>
                [C.ash, C.belle, C.ogre, C.imperm, C.veiler, C.dominus, C.nibiru].includes(normal(c.code)) ||
                codeIs(c, C.sword) && opposing.location === L.monster)) return;
            const search = msg.chains.find(c => codeIs(c, C.curse) && c.desc === C.curse * 16);
            return msg.prepareResponse(search);
        }
        if (name === 'SelectCard' && msg.min === 1) {
            const cards = msg.cards;
            if (source === C.rescue || source === C.sword) return cardPick(msg, cards, [C.curse], L.deck);
            if (source === C.formation) return cardPick(msg, cards, [C.fighter], L.grave);
            if (source === C.curse) {
                return cardPick(msg, cards, [C.sword], L.extra) ?? cardPick(msg, cards, [C.fighter], L.deck);
            }
        }
        if (name === 'SelectUnselectCard' && source === C.curse && s.hint === S.HINTMSG_FMATERIAL) {
            const pick = [C.princess, C.fighter].find(code => !msg.unselectableCards.some(c => codeIs(c, code)));
            if (pick) {
                const i = msg.selectableCards.findIndex(c => codeIs(c, pick) && c.location === L.hand);
                if (i >= 0) {
                    this.swordSummoning = true;
                    return msg.prepareResponse(P.IndexResponse(i));
                }
            }
        }
        if (name === 'SelectPlace' && msg.count <= 1) {
            const legal = msg.getSelectablePlaces();
            const place = legal.find(p => p.location === L.monster &&
                (source === C.curse && this.swordSummoning ? p.sequence >= 5 : p.sequence < 5));
            if (place) return msg.prepareResponse([place]);
        }
    }
}

module.exports = { MainPhaseLethal };
