'use strict';

const P = require('ygopro-msg-encode');
const { nameOf } = require('../tests/engine-search-protocol.cjs');
const { optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { planCombat } = require('../magistus/public-combat.cjs');

const C = {
    chief: 50486289, fighter: 101307006, curse: 101307007, harpie: 101307008, rose: 65302903,
    princess: 84539520, queen: 15951532, call: 57312333, rota: 32807846, mirror: 101307053,
    rescue: 101307074, formation: 101307054, sword: 101307032, empress: 23965033, dragon: 101307050,
    baronne: 84815190, george: 70522875, legion: 12496261, shrimp: 92744676, prism: 65400671,
    dugares: 66011101, sheep: 50277356, saryuja: 74997493, tornado: 6983839, exciton: 46772449,
    goddess: 98127546, ash: 14558128, belle: 73642296, ogre: 59438930, maxx: 23434539, droll: 94145021,
    veiler: 97268402, imperm: 10045474, dominus: 97045737, nibiru: 27204311,
};
const L = { deck: 1, hand: 2, monster: 4, spell: 8, grave: 16, extra: 64 };
const F = { standby: 2, main1: 4, damage: 32, damageCalc: 64, main2: 256, end: 512 };
const S = P.OcgcoreScriptConstants;
const alias = new Map([[14558127, C.ash], [23434538, C.maxx], [50277355, C.sheep], [C.queen, C.princess]]);
const normal = id => alias.get(id) || id;
const amazon = new Set([C.chief, C.fighter, C.curse, C.princess, C.harpie, C.sword, C.empress]);
const starters = new Set([C.curse, C.chief, C.fighter, C.princess]);
const handTraps = new Set([C.ash, C.belle, C.ogre, C.maxx, C.droll, C.veiler, C.imperm, C.dominus, C.nibiru]);
const searchPolicy = {
    includeSequence: true, handTraps, normalize: normal, idleWeight: () => 1, choicePriority: () => 1,
};

function effectIndex(code, desc) {
    if (!desc || desc === 221) return -1;
    for (const id of [code, normal(code)]) {
        const offset = desc - id * 16;
        if (offset >= 0 && offset < 16) return offset;
    }
    return -1;
}

const threat = card => card && Math.max(0, (card.position & 1 ? card.attack : card.defense) ?? 0);
const other = view => view.enemy.filter(card => card.location === L.monster);
const own = view => view.own.filter(card => card.location === L.monster);
const backrow = view => view.enemy.filter(card => card.location === L.spell);
const faceup = card => Boolean(card.position & 5);
const active = card => faceup(card) && !(card.status & S.STATUS_DISABLED);
const last = state => [...state.chains.values()].at(-1);
const coversMonster = new Set([C.sword, C.imperm, C.veiler, C.baronne, C.rescue, C.formation, C.ash, C.dominus, C.nibiru]);
const isMain = phase => phase === F.main1 || phase === F.main2;

function attackProjection(attackers, defenders) {
    const usable = attackers.filter(c => c.position & 1 || c.type & S.TYPE_LINK);
    if (!defenders.length)
        return usable.reduce((n, c) => n + Math.max(0, c.attack || 0), 0);
    const ordered = [...defenders].sort((a,b)=>threat(b)-threat(a));
    const strikes = usable.map(c => Math.max(0, c.attack || 0)).sort((a, b) => a - b);
    let damage = 0;
    for (const defender of ordered) {
        const power=threat(defender);
        const index = strikes.findIndex(value => value > power);
        if (index < 0) return damage;
        const attack = strikes.splice(index, 1)[0];
        if (defender.position & 1) damage += attack - power;
    }
    return damage + strikes.reduce((n, v) => n + v, 0);
}

function timing(id, index, location, state, view) {
    const ourTurn = state.turnPlayer === state.bot;
    const monsters = own(view);
    const foes = other(view);
    const cause = last(state);
    const enemyEffect = cause && cause.player !== state.bot;
    const onFieldEffect = enemyEffect && cause.location === L.monster &&
        foes.some(card => card.sequence === cause.sequence && active(card));
    // A new opposing link resolves before any response already waiting below it.
    const committed = !enemyEffect && [...state.chains.values()].some(link =>
        link.player === state.bot && coversMonster.has(link.code));
    const immediate = foes.reduce((n,c)=>n+(faceup(c) && (c.position & 1) ? c.attack || 0 : 0),0);
    const underThreat = immediate >= state.lp[state.bot];
    const canBattle = ourTurn && state.turn > 1 && state.phase < F.main2;
    const opens = monsters.filter(c => c.sequence < 5).length < 5;
    const hasTarget = foes.some(c => faceup(c) && c.attack != null);
    switch (id) {
    case C.chief:
        if (location === L.hand) return ourTurn ? 62 : 0;
        if (canBattle && monsters.some(c => !amazon.has(normal(c.code)) && c.attack >= 1800)) return 0;
        return 55;
    case C.fighter:
        if (index !== 2) return 61;
        return state.battle && state.battle.ours < state.battle.theirs &&
            (!ourTurn || state.battle.damage >= state.lp[state.bot] || state.battle.ours > 1200) ? 65 : 0;
    case C.curse:
        if (location !== L.grave) return index === 1 ? 65 : 59;
        return state.battle?.ours < state.battle?.theirs && (ourTurn || underThreat) ? 69 : 0;
    case C.harpie:
        if (location !== L.hand) return 54;
        return isMain(state.phase) && (cause?.player === state.bot && (cause.code===C.rescue && state.lastTargeted!=null ||
            cause.code===C.curse && monsters.some(c=>starters.has(normal(c.code))))
            || view.hand.includes(C.chief) && monsters.some(c=>c.code===C.chief)
            || state.lastTargeted!=null && monsters.some(c=>c.sequence===state.lastTargeted)
            || !ourTurn && !view.own.some(c=>c.location===L.spell)) ? 43 : 0;
    case C.rose:
        if (location === L.hand)
            return !ourTurn && !view.hand.includes(C.dominus) && (view.hand.length > 1 || underThreat) ? 16 : 0;
        return 32;
    case C.princess:
        if (index !== 1 || state.phase < 8) return 52;
        return !state.calledTarget && (state.battle?.ours >= state.battle?.theirs || monsters.length < 2) ? 29 : 0;
    case C.rota:
    case C.mirror:
        return ourTurn ? 49 : 0;
    case C.call: {
        if (location !== L.grave) return ourTurn ? 54 : 0;
        if (!canBattle || foes.length < 2) return 0;
        const attackers = state.chiefLock ? monsters.filter(c => amazon.has(normal(c.code))) : monsters;
        if (foes.every(faceup) && attackProjection(attackers, foes) >= state.lp[1-state.bot]) return 0;
        return monsters.some(attacker => amazon.has(normal(attacker.code)) && attacker.position & 1 &&
            foes.every(defender => attacker.attack > threat(defender))) ? 48 : 0;
    }
    case C.formation:
        if (foes.length === 1 && committed || !opens) return 0;
        if (foes.length && monsters.some(c => faceup(c) && amazon.has(normal(c.code)))) return 74;
        return !monsters.length && (view.grave.includes(C.chief) || view.grave.includes(C.fighter)) &&
            (underThreat || foes.length > 0 || state.phase === F.end) ? 27 : 0;
    case C.rescue:
        if (location === L.grave)
            return view.grave.includes(C.fighter) && (isMain(state.phase) || underThreat || state.phase === F.end)
                || view.grave.includes(C.harpie) && isMain(state.phase) && monsters.some(c=>c.attribute & 9) ? 41 : 0;
        if (foes.length === 1 && committed || !opens) return 0;
        return foes.length ? 72 : ourTurn && monsters.some(c=>[C.fighter,C.chief].includes(c.code)) ? 28 : 0;
    case C.sword:
        if (index !== 2) return 65;
        if (onFieldEffect && !committed) return 95;
        return state.battle && state.battle.theirs - state.battle.ours > 0 &&
            state.battle.theirs - state.battle.ours <= 500 && monsters.length > 1 ? 83 : 0;
    case C.empress: return 58;
    case C.dragon: return ourTurn && view.grave.includes(C.princess) && view.grave.includes(C.sword) ? 63 : 0;
    case C.baronne:
        if (index === 1) return enemyEffect && !committed ? 90 : 0;
        if (index === 2) return !monsters.some(c=>c.code===C.baronne && !state.spentBaronne.has(c.sequence)) &&
            (view.grave.includes(C.sword) || view.grave.includes(C.chief)) && !underThreat ? 37 : 0;
        return foes.length || backrow(view).length ? 79 : 0;
    case C.george: return index === 1 ? enemyEffect && !committed ? 94 : 0 : 66;
    case C.legion:
    case C.sheep:
    case C.saryuja:
        return index === 1 && id === C.saryuja && !view.hand.some(c=>starters.has(c)) ? 0 : 55;
    case C.prism:
        return index !== 1 ? 63 : !ourTurn && monsters.length <= 3 && (underThreat || state.phase >= 8) ? 34 : 0;
    case C.shrimp:
        if (index !== 1) return backrow(view).length ? 54 : 0;
        return !ourTurn && monsters.some(c => c.code !== C.shrimp && ((c.level || 0) & 255) + 2 === 10) ? 68 : 0;
    case C.dugares: return ourTurn ? 47 : 0;
    case C.tornado: return backrow(view).length && !(committed && backrow(view).length === 1) ? 76 : 0;
    case C.exciton:
        return foes.length + backrow(view).length > view.own.length - 1 &&
            (!canBattle || attackProjection(monsters,foes) < state.lp[1-state.bot]) ? 77 : 0;
    case C.goddess: return index === 1 ? enemyEffect && !committed ? 86 : 0 : hasTarget ? 51 : 0;
    case C.ash: return enemyEffect && (!committed || cause.code === C.maxx) ? 82 : 0;
    case C.belle: return enemyEffect && !committed ? 72 : 0;
    case C.ogre: return enemyEffect && !committed && (cause.location === L.spell || foes.length > 1 || underThreat) ? 57 : 0;
    case C.maxx: return !ourTurn && !state.maxxUsed && (state.enemySummons || isMain(state.phase)) ? 66 : 0;
    case C.droll: return !ourTurn && !state.maxxUsed && !state.drollUsed && state.phase <= F.main1 ? 48 : 0;
    case C.veiler:
    case C.imperm: return onFieldEffect && !committed ? (location === L.spell ? 85 : 74) : 0;
    case C.dominus:
        if (!enemyEffect || committed) return 0;
        return location === L.spell || underThreat || monsters.some(c=>c.code===C.baronne) && foes.length >= 2 ? 73 : 0;
    case C.nibiru: {
        if (ourTurn || committed) return 0;
        if (underThreat) return 84;
        const otherResponse = monsters.some(c => active(c) && (
            c.code === C.sword && !state.swordNegateUsed ||
            c.code === C.baronne && !state.spentBaronne.has(c.sequence) ||
            c.code === C.george && !state.georgeNegateUsed && view.own.some(spell =>
                spell.location === L.spell && faceup(spell) && spell.equipCard?.location === L.monster)
        ));
        return !otherResponse && foes.length >= Math.max(2, monsters.length + 1) ? 84 : 0;
    }
    default: return 0;
    }
}

function targetWeight(c, state, view) {
    const source = state.solving || last(state);
    const id = source?.code || state.lastIdle;
    const hint = state.hint;
    const enemy = c.controller !== state.bot;
    const card = normal(c.code);
    if (hint === S.HINTMSG_ATTACKTARGET) {
        const attacker = own(view).find(a => a.sequence === state.attacker);
        const defender = other(view).find(a => a.sequence === c.sequence);
        if (!defender || !attacker) return 12;
        if (state.preferredTarget===c.sequence) return 110;
        const margin = (attacker.attack || 0) - threat(defender);
        if (!faceup(defender)) return attacker.attack >= 2400 ? 30 : 10;
        return margin > 0 ? 40 + Math.floor(margin / 200) : 1;
    }
    if (enemy && [L.monster,L.spell].includes(c.location)) {
        const actual = view.enemy.find(a=>a.location===c.location && a.sequence===c.sequence);
        if ([C.sword,C.imperm,C.veiler].includes(state.pendingEffect)) {
            const target=[...state.chains.values()].reverse().find(link=>link.player!==state.bot && link.location===L.monster);
            if (target?.sequence===c.sequence) return 105;
        }
        return 42 + Math.floor(threat(actual) / 250);
    }
    if (hint === S.HINTMSG_RELEASE || hint === S.HINTMSG_TOGRAVE) {
        if (card === C.sword || card === C.baronne || card === C.george) return 2;
        return c.location === L.monster ? 29 : 7;
    }
    if (id === C.harpie && c.location === L.monster) {
        if (state.lastTargeted === c.sequence) return 100;
        return card === C.chief ? 55 : card === C.sword ? 2 : 16;
    }
    if (hint === S.HINTMSG_DISCARD) {
        if (id === C.dugares && [C.ash,C.ogre,C.belle,C.rose].includes(card)) return 75;
        return handTraps.has(card) ? 27 : 5;
    }
    if (id===C.saryuja && c.location===L.hand && hint===S.HINTMSG_TODECK) {
        const origin=state.handOrigins?.[c.sequence];
        return origin?.source===source ? 2 : handTraps.has(card) || !starters.has(card) ? 85 : 21;
    }
    if (c.location === L.extra) {
        if (card === C.empress && (!state.chiefLock || other(view).length)) return 85;
        if (card === C.sword || card === C.shrimp || card === C.legion) return 70;
    }
    if (c.location === L.grave && id === C.sheep && [C.ash,C.ogre,C.belle,C.rose].includes(card)) return 68;
    if (id === C.dragon && hint === S.HINTMSG_FMATERIAL && [C.princess,C.sword].includes(card)) return 80;
    if ([C.call,C.rota,C.curse].includes(id) && c.location === L.deck && card === C.fighter) return 42;
    if ([C.curse,C.sword,C.empress,C.rescue].includes(id) && card === C.chief && !view.hand.includes(C.chief)) return 48;
    if ([C.rescue,C.formation].includes(card)) return view.own.some(s=>s.code===card) ? 6 : 70;
    if (card === C.mirror || card === C.harpie || card === C.fighter) return 35;
    return 13;
}

function summonPosition(msg, state, options) {
    const defense = options.find(option => option.key === '4');
    if (!defense) return options[0];
    if (msg.code === C.nibiru + 1) return defense;

    const stats = state.cardReader?.(msg.code);
    const settingUp = state.turnPlayer === state.bot &&
        (state.phase === F.main2 || (state.turn === 1 && state.phase === F.main1));
    if (stats && (stats.attack === 0 || (settingUp &&
        (stats.attack < 2000 || stats.attack <= stats.defense)))) return defense;
    return options.find(option => option.key === '1') || options[0];
}

function choices(msg, state, view) {
    const name = nameOf(msg);
    const ourTurn = state.turnPlayer === state.bot;
    const defenders = other(view);
    const attackers = own(view);
    const m2 = state.phase === F.main2;
    const disabled=(id,location,sequence)=>view.own.some(c=>c.location===location&&c.sequence===sequence&&
        normal(c.code)===normal(id)&&(c.status&S.STATUS_DISABLED));
    let options;
    if (name === 'SelectBattleCmd') {
        const B = P.BattleCmdType;
        const plan=planCombat(msg.attackableCards.map(c=>({ sequence:c.sequence, direct:Boolean(c.directAttack),
            attack:attackers.find(a=>a.sequence===c.sequence)?.attack||0 })),defenders.map(c=>({
            sequence:c.sequence, strength:faceup(c)?threat(c):1800,defending:!(c.position&1),
        })),state.lp[1-state.bot],true);
        state.preferredTarget=plan.target;
        options = msg.attackableCards.map((c,i) => {
            const attacker = attackers.find(a=>a.sequence===c.sequence);
            const attack = attacker?.attack || 0;
            const good = defenders.some(c => faceup(c) ? attack > threat(c) : attack >= 2200);
            return { key: `attack:${c.code}:${c.sequence}:${c.directAttack}`,
                response: msg.prepareResponse(B.ATTACK,P.IndexResponse(i)),
                weight: plan.attacker===c.sequence ? (plan.win?130:85) :
                    c.directAttack || !defenders.length ? 70 + attack / 100 : good ? 54 + attack / 200 : 0 };
        });
        options.push(...msg.activatableCards.map((c,i)=>({
            key:`battle:${c.code}:${c.desc}`, response:msg.prepareResponse(B.ACTIVATE,P.IndexResponse(i)),
            weight:disabled(c.code,c.location,c.sequence)?0:timing(normal(c.code),effectIndex(c.code,c.desc),c.location,state,view),
        })));
        if (msg.canM2) options.push({ key:'main2',response:msg.prepareResponse(B.TO_M2),weight:13 });
        if (msg.canEp && !msg.canM2) options.push({ key:'end',response:msg.prepareResponse(B.TO_EP),weight:12 });
        return options.filter(o=>o.weight>0);
    }
    if (name === 'SortCard' || name === 'SortChain') {
        const sequence = msg.cards.map((_,i)=>P.IndexResponse(i));
        return [{ key:'sort',response:msg.prepareResponse(sequence),weight:1 }];
    }
    const viewMessage = typeof msg.playerView === 'function' ? msg.playerView(state.bot) : msg;
    options = optionsFor(viewMessage,state);
    if (name === 'SelectPosition') {
        const position = summonPosition(msg, state, options);
        return position ? [position] : [];
    }
    return options.map(o=>{
        let weight = o.weight;
        if (name === 'SelectIdleCmd') {
            if (o.key === 'end') weight = m2 || !msg.canBp ? (state.enemyDraws >= 2 ? 43 : 13) : 0;
            else if (o.key === 'battle') {
                const available=state.chiefLock?attackers.filter(c=>amazon.has(normal(c.code))):attackers;
                weight=attackProjection(available,defenders)>=state.lp[1-state.bot] && !backrow(view).length ? 95 :
                    available.some(c=>c.position&1) ? 37 : 8;
            }
            else {
                const [type,cardKey,raw] = o.key.split('/');
                const [id,,location,sequence] = cardKey.split(':').map(Number);
                const code = normal(id);
                switch (Number(type)) {
                case P.IdleCmdType.SUMMON: weight = starters.has(code) ? 49 : code === C.rose ? 17 : 0; break;
                case P.IdleCmdType.SPSUMMON:
                    weight = [C.baronne,C.george].includes(code) ? 83 : code===C.prism ? 53 :
                        code===C.goddess && defenders.length ? 51 : code===C.dragon && view.grave.includes(C.princess) &&
                        view.grave.includes(C.sword) ? 64 : [C.dugares,C.sheep,C.saryuja,C.tornado,C.exciton].includes(code) ? 24 : 4;
                    if (code===C.sheep && attackers.some(c=>c.code===C.sheep)) weight=2;
                    break;
                case P.IdleCmdType.ACTIVATE:
                    weight = disabled(id,location,sequence)?0:timing(code,effectIndex(id,Number(raw)),location,state,view);
                    break;
                case P.IdleCmdType.SSET:
                    weight = [C.rescue,C.formation,C.imperm].includes(code) && !view.own.some(c=>c.code===code) ? (m2 ? 50 : 21) : 0;
                    break;
                case P.IdleCmdType.REPOS:
                    weight = ourTurn && state.phase===F.main1 && attackers.some(c=>c.code===code&&c.position&4) &&
                        (attackProjection(attackers.map(c=>c.code===code?{...c,position:1}:c),defenders) >= state.lp[1-state.bot]) ? 60 : 0;
                    break;
                default: weight = 0;
                }
            }
        } else if (name === 'SelectChain') {
            if (o.key === 'pass') weight = 10;
            else {
                const [code,desc]=o.key.split(':').map(Number);
                const forced=msg.chains.some(c=>c.forced);
                weight=forced ? 50 : disabled(code,o.chainLocation,o.chainSequence)?0:
                    timing(normal(code),effectIndex(code,desc),o.chainLocation,state,view);
            }
        } else if (name === 'SelectEffectYn' || name === 'SelectYesNo') {
            const id = msg.code || state.solving?.code || state.lastIdle || 0;
            const allowed=disabled(id,msg.location,msg.sequence)?0:
                timing(normal(id),effectIndex(id,msg.desc),msg.location ?? L.monster,state,view);
            weight=o.key.endsWith(':true') ? allowed : 12;
        } else if (name === 'SelectOption') {
            const desc=Number(o.key);
            if (desc===C.formation*16+3) weight=defenders.length>1 ? 83 : 50;
            if (desc===C.formation*16+2) weight=defenders.length===1 ? 83 : 57;
            if (desc===C.dugares*16) weight=52;
            if (desc===C.dugares*16+1 && !view.hand.length && view.grave.includes(C.sword)) weight=34;
            if (desc===C.dugares*16+2 && state.phase===F.main1 && state.turn>1 &&
                attackers.some(c=>(c.attack||0)*2>=state.lp[1-state.bot])) weight=86;
            if (desc===1190) weight=48;
            if (desc===1191 && state.lastIdle===C.call && view.own.some(c=>c.code===C.formation) &&
                !view.grave.some(c=>amazon.has(c))) weight=55;
        } else if (name === 'SelectPlace' && state.solving && [C.curse,C.mirror,C.formation].includes(state.solving.code)) {
            const sheep=attackers.find(c=>c.code===C.sheep && c.sequence>=5);
            const [,location,sequence]=o.key.split(':').map(Number);
            if (sheep && location===L.monster && (sheep.sequence===5?[0,2]:[2,4]).includes(sequence)) weight=60;
        } else if (o.selectedCards?.length) {
            weight=o.selectedCards.reduce((n,c)=>n+targetWeight(c,state,view),0)/o.selectedCards.length;
        }
        return {...o,weight};
    }).filter(o=>o.weight>0);
}

module.exports = { C, L, F, amazon, normal, timing, searchPolicy, choices, attackProjection, summonPosition };
