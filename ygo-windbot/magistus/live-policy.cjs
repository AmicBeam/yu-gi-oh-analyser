'use strict';

const P = require('ygopro-msg-encode');
const { optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { card, trap, drawTraps, ownPolicy, ownDefensiveEffects, settledScore } =
    require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');
const { nameOf } = require('../tests/engine-search-protocol.cjs');
const { context, value } = require('./offline.cjs');
const { chainRules, finishSet, isEndSet, selectionRules } = require('./live-rules.cjs');
const { actionable, features: secondFeatures, battleOptions, actionPrior, learned: secondValue } = require('./second-turn.cjs');
const { mergeMagistus } = require('../Models/pack-turn-models.cjs');
const { OutlawPlan } = require('./main-phase-plan.cjs');
const { planCombat } = require('./public-combat.cjs');

const MZONE = 4, SZONE = 8, HAND = 2, GRAVE = 16, EXTRA = 64;
const MAIN_ONE = 4, MAIN_TWO = 256, FACE_UP_DEFENSE = '4', NIBIRU_TOKEN = 27204312;
const DISABLED = 0x1;
const SORCERER_REVIVE = card.sorcerer * 16 + 1;
const extraTraps = new Set([14558128, 94145022, card.droll, 73642296, 59438930, 97268402, 97045737, 40366667]);
const defenses = new Set([...ownDefensiveEffects, ...extraTraps, card.littleKnight, trap.nibiru]);
const softPass = step => step.name === 'SelectChain' && step.key === 'pass';
const cardsIn = list => list.map(c => c?.alias || c?.code || 0).filter(Boolean);
const publicMonster = c => c?.code && (c.position & 5) ? c : undefined;

class MagistusPolicy {
    constructor(seat, cardReader, guides, model, log, extra = [], secondModel) {
        this.seat = seat;
        this.guides = guides;
        this.model = secondModel && !model ? mergeMagistus({ weights: {} }, secondModel) :
            model && mergeMagistus(model, secondModel);
        this.tactics = this.model?.tactics;
        this.log = log;
        this.turnActor = -1;
        this.initialized = false;
        this.routes = [];
        this.field = [];
        this.enemyField = [];
        this.enemyMonsters = [];
        this.enemyBackrow = [];
        this.lp = [8000, 8000];
        this.spells = [];
        this.hand = [];
        this.grave = [];
        this.extra = [...extra];
        this.drawPressure = new Set();
        this.ownDrawPressure = new Set();
        this.threatenedOwn = new Set();
        this.disabledGlass = new Set();
        this.firstOwnTurn = true;
        this.initialOwnTurn = 0;
        this.repeat = new Map();
        this.secondOverrides = 0;
        this.outlawPlan = new OutlawPlan();
        this.outlawPlan.reset();
        this.state = { cardReader, searchPolicy: ownPolicy(0, true), trace: [], events: [],
            turn: 0, phase: 0, summons: 0, pending: 0, board: [], hand: [], grave: [],
            chains: new Map(), draws: 0, lost: 0, spellCount: 0, trapActions: [], declarations: [] };
    }

    own(player) { return player === this.seat; }

    goingSecond() {
        return this.own(this.turnActor) && Boolean(this.tactics || this.exploreSecond);
    }

    refresh() {
        const s = this.state;
        s.board = cardsIn(this.field);
        s.hand = this.hand.filter(Boolean);
        s.grave = this.grave.filter(Boolean);
        s.spellCount = cardsIn(this.spells).length + s.grave.filter(id => s.cardReader(id)?.type & 2).length;
        s.trapCount = s.trapActions.length;
        s.declarations = [...new Set(s.hand)];
    }

    move(msg) {
        const { previous, current } = msg;
        if (!this.own(previous.controller) && previous.location === SZONE)
            delete this.enemyBackrow[previous.sequence];
        if (!this.own(current.controller) && current.location === SZONE)
            this.enemyBackrow[current.sequence] = true;
        if (this.own(previous.controller) && previous.location === MZONE)
            this.disabledGlass.delete(previous.sequence);
        if (this.own(current.controller) && current.location === MZONE) {
            this.disabledGlass.delete(current.sequence);
            const source = this.state.solving;
            if (msg.code === card.verre && (previous.location === HAND || previous.location === GRAVE) &&
                source?.player === 0 && source.code === card.sorcerer && source.desc === SORCERER_REVIVE) {
                // SRVPro2 can offer the summon trigger before it sends QUERY_STATUS.
                this.disabledGlass.add(current.sequence);
            }
        }
        if (!this.own(msg.previous.controller) && msg.previous.location === MZONE) {
            delete this.enemyField[msg.previous.sequence];
            delete this.enemyMonsters[msg.previous.sequence];
        }
        if (!this.own(msg.current.controller) && msg.current.location === MZONE) {
            this.enemyField[msg.current.sequence] = publicMonster({ code: msg.code, position: msg.current.position });
            this.enemyMonsters[msg.current.sequence] = true;
        }
        if (this.own(msg.previous.controller) && msg.previous.location === EXTRA) {
            const index = this.extra.indexOf(msg.code);
            if (index >= 0) this.extra.splice(index, 1);
        }
        if (this.own(msg.current.controller) && msg.current.location === EXTRA && msg.code)
            this.extra.push(msg.code);
        const zones = { [MZONE]: this.field, [SZONE]: this.spells, [HAND]: this.hand, [GRAVE]: this.grave };
        const from = this.own(msg.previous.controller) && zones[msg.previous.location];
        if (from) {
            const index = msg.previous.sequence;
            if ([MZONE, SZONE].includes(msg.previous.location)) delete from[index];
            else from.splice(index, 1);
        }
        const to = this.own(msg.current.controller) && zones[msg.current.location];
        if (to && msg.code) {
            const index = msg.current.sequence;
            const c = { code: msg.code, position: msg.current.position };
            if ([MZONE, SZONE].includes(msg.current.location)) to[index] = c;
            else to.splice(index, 0, msg.code);
        }
        if (msg.previous.controller !== this.seat && msg.previous.location === HAND &&
            msg.current.location !== HAND && (msg.reason & 0x4040) === 0x4040 &&
            this.state.solving?.player === 0) this.state.lost++;
        this.refresh();
    }

    glassIsDisabled(controller, location, sequence) {
        if (!this.own(controller) || location !== MZONE) return false;
        const glass = this.field[sequence];
        return this.disabledGlass.has(sequence) || (glass?.code === card.verre && (glass.status & DISABLED) !== 0);
    }

    observe(msg) {
        const name = nameOf(msg), s = this.state;
        switch (name) {
        case 'Start':
            this.lp = [msg.startLp0, msg.startLp1];
            break;
        case 'Damage':
            this.lp[msg.player] -= msg.value;
            break;
        case 'PayLpCost':
            this.lp[msg.player] -= msg.cost;
            break;
        case 'Recover':
            this.lp[msg.player] += msg.value;
            break;
        case 'NewTurn':
            this.outlawPlan.reset();
            if (s.turn > 0) this.log({ event: 'turn-ended', turn: s.turn, board: s.board, draws: s.draws, lost: s.lost,
                set: cardsIn(this.spells.filter(c => c && (c.position & 10))) });
            s.turn++;
            this.turnActor = msg.player;
            s.phase = s.summons = s.pending = s.draws = 0;
            this.drawPressure.clear();
            this.ownDrawPressure.clear();
            this.threatenedOwn.clear();
            this.tearsReactiveUsed = false;
            this.normalUsed = this.secondInterrupted = false;
            this.secondOverrides = 0;
            this.closing = false;
            this.intent = undefined;
            this.repeat.clear();
            if (this.own(msg.player)) {
                this.firstOwnTurn = !this.initialOwnTurn;
                if (this.firstOwnTurn) this.initialOwnTurn = s.turn;
            }
            if (this.initialOwnTurn && s.turn > this.initialOwnTurn + 1) this.routes = [];
            break;
        case 'NewPhase':
            s.phase = msg.phase;
            break;
        case 'Move':
            this.move(msg);
            break;
        case 'PosChange': {
            if (msg.card.location !== MZONE) break;
            const { controller, sequence } = msg.card;
            if (this.own(controller) && (msg.currentPosition & 10)) this.disabledGlass.delete(sequence);
            if (this.own(controller))
                this.field[sequence] = { ...this.field[sequence], code: msg.code, position: msg.currentPosition };
            else
                this.enemyField[sequence] = publicMonster({ code: msg.code, position: msg.currentPosition });
            this.refresh();
            break;
        }
        case 'BecomeTarget':
            if ((s.solving || [...s.chains.values()].at(-1))?.player !== 1) break;
            for (const target of msg.targets) {
                const player = target & 0xff, location = target >>> 8 & 0xff, sequence = target >>> 16 & 0xff;
                const monster = this.field[sequence];
                if (this.own(player) && location === MZONE && monster?.code)
                    this.threatenedOwn.add(sequence);
            }
            break;
        case 'UpdateData':
            if (!this.own(msg.player)) {
                if (msg.location === MZONE) {
                    this.enemyField = msg.cards.map(publicMonster);
                    this.enemyMonsters = msg.cards.map(c => Boolean(c && !c.empty));
                }
                if (msg.location === SZONE) this.enemyBackrow = msg.cards.map(c => Boolean(c && !c.empty));
                break;
            }
            if (msg.location === MZONE) this.field = msg.cards.slice();
            if (msg.location === SZONE) this.spells = msg.cards.slice();
            if (msg.location === HAND) this.hand = cardsIn(msg.cards);
            if (msg.location === GRAVE) this.grave = cardsIn(msg.cards);
            this.refresh();
            break;
        case 'UpdateCard':
            if (!this.own(msg.controller)) {
                if (msg.location === MZONE) {
                    this.enemyField[msg.sequence] = publicMonster(msg.card);
                    this.enemyMonsters[msg.sequence] = Boolean(msg.card && !msg.card.empty);
                }
                if (msg.location === SZONE) this.enemyBackrow[msg.sequence] = Boolean(msg.card && !msg.card.empty);
                break;
            }
            if (msg.location === MZONE) this.field[msg.sequence] = msg.card;
            if (msg.location === SZONE) this.spells[msg.sequence] = msg.card;
            if (msg.location === HAND && msg.card.code) this.hand[msg.sequence] = msg.card.code;
            this.refresh();
            break;
        case 'Draw':
            if (this.own(msg.player)) {
                this.hand.push(...(msg.cards || []).map(code => code & 0x7fffffff));
                this.refresh();
            } else if (this.own(this.turnActor)) s.draws += msg.count ?? msg.cards.length;
            break;
        case 'Summoning':
        case 'SpSummoning':
            if (this.own(msg.controller)) {
                s.pending++;
                if (name === 'Summoning') this.normalUsed = true;
            }
            break;
        case 'Summoned':
        case 'SpSummoned':
            s.summons += s.pending;
            s.pending = 0;
            break;
        case 'Chaining': {
            const player = this.own(msg.controller) ? 0 : 1;
            s.chains.set(msg.chainCount, { player, location: msg.location, sequence: msg.sequence,
                code: msg.code, desc: msg.desc });
            this.threatenedOwn.clear();
            if (player === 0) {
                s.chainSource = msg.code;
                if (msg.code === card.tears && this.intent?.reason === 'tears-reactive') this.tearsReactiveUsed = true;
            } else if (defenses.has(msg.code) || drawTraps.has(msg.code) || msg.code === trap.princess)
                s.trapActions.push([msg.code, s.lastIdle || 0, s.summons]);
            if (player === 1 && this.own(this.turnActor)) this.secondInterrupted = true;
            s.events.push(['effect', s.turn, player, msg.code, msg.location, msg.desc]);
            s.trace.push(['Chaining', msg.code, player]);
            break;
        }
        case 'ChainSolving':
            s.solving = s.chains.get(msg.chainCount);
            if (s.solving?.player === 0) s.chainSource = s.solving.code;
            break;
        case 'ChainNegated':
        case 'ChainDisabled': {
            const stopped = s.chains.get(msg.chainCount);
            const response = [...s.chains.entries()].reverse().find(([i, link]) =>
                i > msg.chainCount && link.player === 0 && [card.savage, card.dragon, card.outlaw].includes(link.code));
            if (stopped?.player === 1 && response)
                s.events.push(['coverage', s.turn, 0, stopped.code, response[1].code]);
            s.chains.delete(msg.chainCount);
            break;
        }
        case 'ChainSolved': {
            const chain = s.chains.get(msg.chainCount);
            if (chain?.player === 0 && [card.moon, card.nation, card.tears].includes(chain.code))
                s.events.push(['resource', s.turn, 0, chain.code]);
            if (chain && drawTraps.has(chain.code)) {
                const pressure = chain.player === 1 ? this.drawPressure : this.ownDrawPressure;
                pressure.add(chain.code);
            }
            s.solving = null;
            break;
        }
        case 'ChainEnd':
            s.chains.clear();
            s.solving = s.chainSource = null;
            this.threatenedOwn.clear();
            this.intent = undefined;
            break;
        case 'Hint':
            if (this.own(msg.player) && msg.type === 3) s.selectionHint = msg.desc;
            break;
        }
    }

    key(name, key) {
        if (this.seat === 0) return key;
        if (name === 'SelectPlace' || name === 'SelectDisField')
            return key.replace(/(?<!\d)[01](?=:)/g, (id, offset, text) =>
                offset === 0 || text[offset - 1] === ',' ? String(1 - Number(id)) : id);
        if (['SelectIdleCmd', 'SelectCard', 'SelectTribute', 'SelectUnselectCard'].includes(name))
            return key.replace(/(\d+):([01]):(\d+)/g, (_, id, player, zone) => `${id}:${1 - Number(player)}:${zone}`);
        return key;
    }

    guide(name, options) {
        const s = this.state;
        if (!this.initialized && this.firstOwnTurn && this.own(this.turnActor) && s.hand.length >= 5) {
            this.initialized = true;
            const held = [...s.hand];
            this.routes = this.guides.filter(g => {
                const used = [...held];
                return g.core.every(id => {
                    const i = used.indexOf(id);
                    if (i < 0) return false;
                    used.splice(i, 1);
                    return true;
                });
            }).map(route => ({ route, i: 0 }));
        }
        const match = [];
        for (const item of this.routes) {
            const { route } = item;
            let i = item.i;
            // A room may omit an optional chain window when neither side has a response.
            while (i < route.steps.length && softPass(route.steps[i]) && name !== 'SelectChain') i++;
            const step = route.steps[i];
            if (!step) continue;
            let choice = step.name === name && options.filter(option => option.key === step.key)[step.occurrence || 0];
            if (!choice && name === 'SelectChain') choice = options.find(o => o.key === 'pass');
            else if (choice) i++;
            if (!choice) continue;
            const pressure = s.trapActions.length > 0 && s.trapActions.some(a => a[0] === route.pressure);
            const relevance = pressure ? 200 + route.core.length * 5 : route.pressure ? 0 : 100;
            match.push({ route, i, choice, relevance });
        }
        match.sort((a, b) => b.relevance - a.relevance || b.route.score - a.route.score || b.i - a.i);
        const best = match[0];
        if (best) {
            this.routes = match.filter(item => item.choice === best.choice);
            if (name === 'SelectIdleCmd') this.log({ event: 'guided', route: best.route.id, action: best.choice.key });
            return best.choice;
        }
        this.routes = [];
        return undefined;
    }

    battle(msg) {
        const plannedAttack = msg.attackableCards.some(attacker => {
            if (attacker.directAttack) return true;
            const attack = this.field[attacker.sequence]?.attack ?? this.state.cardReader(attacker.code)?.attack ?? 0;
            return this.enemyField.some(defender => defender && attack > (defender.position & 4
                ? defender.defense ?? this.state.cardReader(defender.code)?.defense ?? Infinity
                : defender.attack ?? this.state.cardReader(defender.code)?.attack ?? Infinity));
        });
        if (!plannedAttack && this.enemyField.filter(Boolean).length === this.enemyMonsters.filter(Boolean).length
            && !msg.activatableCards.length) {
            if (msg.canM2) return msg.prepareResponse(P.BattleCmdType.TO_M2);
            if (msg.canEp) return msg.prepareResponse(P.BattleCmdType.TO_EP);
        }
        if (this.goingSecond()) {
            const options = battleOptions(msg, this);
            if (options.length) {
                const lethal = options.filter(o => o.lethal);
                const attack = o => this.field[o.attackSequence]?.attack ??
                    this.state.cardReader(msg.attackableCards.find(c => c.sequence === o.attackSequence)?.code ?? 0)?.attack ?? 0;
                const plan = !lethal.length && this.publicCombat(options.filter(o => o.attackSequence !== undefined)
                    .map(o => ({ sequence: o.attackSequence, attack: attack(o), direct: o.key.endsWith(':1') })));
                const guards = this.tactics?.combatGuards;
                const safeOnly = this.tactics?.combat === 'battle-safe';
                const usePlan = plan?.attacker !== undefined &&
                    (!safeOnly || (plan.win && !this.enemyBackrow.some(Boolean))) &&
                    (!guards?.onlyLethal || plan.win) && (!guards?.noBackrow || !this.enemyBackrow.some(Boolean)) &&
                    (!guards?.preferEffects || plan.win || !msg.activatableCards.length);
                if (usePlan) {
                    this.attackSequence = plan.attacker;
                    this.combatTarget = plan.target;
                    return options.find(o => o.attackSequence === plan.attacker).response;
                }
                this.combatTarget = undefined;
                const choice = this.rank('SelectBattleCmd', lethal.length ? lethal : options, o => Math.log(o.weight));
                this.attackSequence = choice.attackSequence;
                return choice.response;
            }
        }
        for (const [i, c] of msg.attackableCards.entries()) {
            if (c.directAttack) return msg.prepareResponse(P.BattleCmdType.ATTACK, P.IndexResponse(i));
        }
        if (msg.canM2) return msg.prepareResponse(P.BattleCmdType.TO_M2);
        if (msg.canEp) return msg.prepareResponse(P.BattleCmdType.TO_EP);
        if (msg.attackableCards.length) return msg.prepareResponse(P.BattleCmdType.ATTACK, P.IndexResponse(0));
        if (msg.activatableCards.length) return msg.prepareResponse(P.BattleCmdType.ACTIVATE, P.IndexResponse(0));
        throw new Error('Empty battle response');
    }

    publicCombat(attackers) {
        if (!['public-plan', 'battle-safe'].includes(this.tactics?.combat) || !attackers.length) return undefined;
        const defenders = this.enemyField.flatMap((c, sequence) => {
            if (!c) return [];
            const defending = c.position === 4;
            const strength = defending ? c.defense : c.attack;
            return Number.isFinite(strength) ? [{ sequence, defending, strength }] : [];
        });
        if (defenders.length !== this.enemyMonsters.filter(Boolean).length) return undefined;
        const littleKnight = this.state.events.some(e => e[0] === 'effect' && e[1] === 2 && e[2] === 0 &&
            e[3] === card.littleKnight && e[5] === card.littleKnight * 16);
        return planCombat(attackers, defenders, this.lp[1 - this.seat], !littleKnight);
    }

    rank(name, choices, weight) {
        const second = this.goingSecond() && actionable.has(name);
        const continuation = this.tactics?.training === 'rule-continuation';
        const gate = this.tactics?.overrideGate;
        const safeContext = gate === 'open-board' ? !this.enemyMonsters.some(Boolean) && !this.enemyBackrow.some(Boolean)
            : gate === 'no-backrow' ? !this.enemyBackrow.some(Boolean)
            : gate === 'after-summon' ? this.state.summons > 0 : true;
        const rankable = second && safeContext && (!continuation || (name === 'SelectIdleCmd' && this.state.phase === MAIN_ONE));
        const limit = this.tactics?.overrideLimit;
        const rated = choices.map(option => {
            const keys = rankable ? secondFeatures(this, name, option, continuation) : [];
            const prior = weight(option);
            return { ...option, keys, prior, score: prior + (rankable ? secondValue(this.model, keys.map(key => 'tactics/' + key)) : 0) };
        });
        if (rankable && this.exploreSecond && rated.length > 1) return this.exploreSecond(name, rated);
        const choice = rated.reduce((best, option) => option.score > best.score ? option : best);
        if (!rankable || !Number.isInteger(limit) || limit < 0) return choice;
        const rule = rated.reduce((best, option) => option.prior > best.prior ? option : best);
        if (choice === rule) return choice;
        if (this.secondOverrides >= limit) return rule;
        this.secondOverrides++;
        return choice;
    }

    selectPosition(msg, options) {
        const defense = options.find(option => option.key === FACE_UP_DEFENSE);
        if (!defense) return options[0];
        const s = this.state;
        if (msg.code === NIBIRU_TOKEN) return defense;
        const stats = s.cardReader(msg.code);
        const settingUp = this.own(this.turnActor) &&
            (s.phase === MAIN_TWO || (s.turn === 1 && s.phase === MAIN_ONE));
        // Match the shared summon-position rules in TwinsExecutor and DefaultExecutor.
        if (stats && (stats.attack === 0 || (settingUp &&
            (stats.attack < 2000 || stats.attack <= stats.defense)))) return defense;
        return options[0];
    }

    choose(msg) {
        const name = nameOf(msg), s = this.state;
        const tactical = this.outlawPlan.choose(msg, name, this);
        if (tactical) {
            this.log({ event: 'magistus-outlaw-plan', prompt: name });
            return tactical;
        }
        if (name === 'SelectBattleCmd') return this.battle(msg);
        if (name === 'SelectIdleCmd' && this.own(this.turnActor) && s.phase === MAIN_ONE) {
            const monsters = this.field.filter(c => c?.code);
            const glass = this.field.findIndex(c => c?.code === card.verre);
            const king = msg.spSummonableCards.find(c => c.code === card.king && c.location === EXTRA);
            if (king && glass >= 0 && monsters.length === 1 && this.glassIsDisabled(this.seat, MZONE, glass))
                return msg.prepareResponse(P.IdleCmdType.SPSUMMON, king);
        }
        if (name === 'SelectEffectYn' && msg.code === card.verre &&
            this.glassIsDisabled(msg.controller, msg.location, msg.sequence)) {
            this.log({ event: 'skip-disabled-glass', turn: s.turn, sequence: msg.sequence });
            return msg.prepareResponse(false);
        }
        this.refresh();
        let options = optionsFor(msg, s).map(o => ({ ...o, key: this.key(name, o.key) }));
        if (!options.length) throw new Error('No legal choices for ' + name);
        const second = this.goingSecond();
        if (second && ['public-plan', 'battle-safe'].includes(this.tactics?.combat)) {
            const early = this.tactics.combatGuards?.early;
            const openBoard = !this.enemyMonsters.some(Boolean) && !this.enemyBackrow.some(Boolean);
            const enterEarly = this.tactics.combat === 'public-plan' && early !== false &&
                (early === 'open-board' ? openBoard : early !== 'no-backrow' || !this.enemyBackrow.some(Boolean));
            if (enterEarly && name === 'SelectIdleCmd' && s.phase === MAIN_ONE) {
                const battle = options.find(o => o.key === 'battle');
                if (battle) {
                    const ready = this.field.flatMap((c, sequence) => c?.position === 1 && Number.isFinite(c.attack) &&
                        c.attack > 0 ? [{ sequence, attack: c.attack, direct: false }] : []);
                    if (this.publicCombat(ready)?.win) return battle.response;
                }
            }
            if (name === 'SelectCard' && s.selectionHint === 549 && this.combatTarget !== undefined) {
                const sequence = this.combatTarget;
                this.combatTarget = undefined;
                const target = options.find(o => o.selectedCards?.some(c => !this.own(c.controller) &&
                    c.location === MZONE && c.sequence === sequence));
                if (target) return target.response;
            }
        }
        let choice = this.initialOwnTurn === 2 && this.firstOwnTurn ? undefined : this.guide(name, options);
        if (name === 'SelectPosition') {
            // Advance the guide past this prompt, but let live position policy choose the response.
            choice = this.selectPosition(msg, options);
            this.log({ event: 'summon-position', code: msg.code, legal: msg.positions,
                position: Number(choice.key), turn: s.turn, phase: s.phase, ownTurn: this.own(this.turnActor) });
            return choice.response;
        }
        if (name === 'SelectChain') {
            const { options: allowed, preferred } = chainRules(this, options, choice);
            if (choice && !allowed.includes(choice)) {
                this.routes = [];
                choice = undefined;
            }
            options = allowed;
            if (preferred) {
                if (choice && choice !== preferred.option) this.routes = [];
                choice = preferred.option;
                const previous = [...s.chains.values()].at(-1);
                this.intent = { reason: preferred.reason, target: previous?.player === 1 ? previous : undefined };
                this.log({ event: 'interference-rule', rule: preferred.reason, action: choice.key,
                    turn: s.turn, phase: s.phase, target: this.intent.target?.code });
            }
        }
        const selected = selectionRules(this, name, options);
        if (selected) {
            if (choice && choice !== selected) this.routes = [];
            choice = selected;
        }
        if (!choice) {
            const tags = context(s), previous = [...s.chains.values()].at(-1);
            const weight = option => {
                let prior = option.weight;
                const id = Number(option.key.split(':')[0]);
                if (second && msg.canBp && name === 'SelectIdleCmd' && option.key === 'battle') {
                    const attacking = this.field.some(c => c?.position === 1 && (c.attack ?? s.cardReader(c.code)?.attack ?? 0) > 0);
                    prior = attacking ? 23 : 4;
                }
                if (name === 'SelectChain') {
                    if (option.key === 'pass') prior = 10;
                    else if (defenses.has(id)) prior = previous?.player === 1 || !this.own(this.turnActor) ? 35 : 0.001;
                }
                if (name === 'SelectIdleCmd' && option.key === 'end') {
                    prior = 6;
                    const position = settledScore(this.field.filter(Boolean), s.lost, 0, s.draws, this.spells.filter(Boolean));
                    if (this.drawPressure.size && s.draws >= 2 && position.score < 5 * s.draws) prior = 180;
                }
                if (second) prior = actionPrior(this, name, option, prior);
                const learned = this.model ? value(this.model.weights, name, option.key, tags) : 0;
                const repeats = name === 'SelectIdleCmd' ? this.repeat.get(option.key) || 0 : 0;
                return Math.log(Math.max(0.0001, prior)) + Math.max(-4, Math.min(4, learned)) - 1.4 * repeats;
            };
            const ranked = name === 'SelectIdleCmd' && options.some(option => option.key === 'end')
                ? options.filter(option => !isEndSet(option)) : options;
            choice = this.rank(name, ranked, weight);
            if (name === 'SelectIdleCmd') this.log({ event: 'ranked', model: Boolean(this.model), action: choice.key });
        }
        if (name === 'SelectIdleCmd') {
            const end = options.find(option => option.key === 'end');
            if (end && (choice.key === 'end' || this.closing)) {
                this.closing = true;
                const set = finishSet(this, options);
                choice = set || end;
                if (set) {
                    this.routes = [];
                    this.log({ event: 'end-set', code: set.card, turn: s.turn, phase: s.phase });
                }
            }
            s.lastIdle = choice.card;
            s.selectionHint = undefined;
            this.repeat.set(choice.key, (this.repeat.get(choice.key) || 0) + 1);
        }
        return choice.response;
    }
}

module.exports = { MagistusPolicy };
