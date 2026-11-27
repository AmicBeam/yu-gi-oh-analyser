'use strict';

const P = require('ygopro-msg-encode');
const { nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { standardChooser } = require('./train.cjs');
const { update, features } = require('./full-train.cjs');
const { mergeAmazoness } = require('../Models/pack-turn-models.cjs');
const { MainPhaseLethal } = require('./main-phase-lethal.cjs');
const { C, L, F, amazon, normal, choices, searchPolicy, attackProjection } = require('./duel-policy.cjs');

class AmazonessPolicy {
    constructor(seat, cardReader, model, log, extra = []) {
        this.seat = seat;
        this.cardReader = cardReader;
        this.model = model && mergeAmazoness(model);
        this.log = log;
        this.zones = [[[],[]], [[],[]]];
        this.hand = [];
        this.grave = [];
        this.repeat = new Map();
        this.firstChoice = true;
        this.extra = [...extra];
        this.lethal = new MainPhaseLethal();
        this.lethal.reset();
        this.state = { bot:seat, cardReader, turn:0, phase:0, chains:new Map(), spentBaronne:new Set(), enemySummons:0,
            draw:0, enemyDraws:0, damage:0, lp:[8000,8000], startOppCount:0, interactions:0, trace:[],
            handOrigins:[], declarations:[], searchPolicy };
    }

    region(player,location) { return this.zones[player]?.[location===L.monster?0:1]; }
    own(player) { return player===this.seat; }

    view() {
        const maps = player => [L.monster,L.spell].flatMap(location => this.region(player,location)
            .flatMap((c,sequence) => {
                if (!c || c.empty || c.position===0 && !c.code) return [];
                if (!this.own(player) && !(c.position&5)) return [{location,sequence,position:c.position}];
                const card = c.code && this.cardReader(c.code);
                return [{ ...c, location,sequence,code:normal(c.code),
                    attack:c.attack??card?.attack, defense:c.defense??card?.defense,
                    level:c.level??card?.level, attribute:c.attribute??card?.attribute,type:c.type??card?.type }];
            }));
        return { own:maps(this.seat), enemy:maps(1-this.seat), hand:this.hand.map(normal), grave:this.grave.map(normal) };
    }

    observe(msg) {
        const name=nameOf(msg);
        const state=this.state;
        const fromTo=(part,action)=>{
            if ([L.monster,L.spell].includes(part.location))
                action(this.region(part.controller,part.location),part.sequence,true,part.position);
            if (this.own(part.controller) && part.location===L.hand) action(this.hand,part.sequence,false,part.position);
            if (this.own(part.controller) && part.location===L.grave) action(this.grave,part.sequence,false,part.position);
        };
        switch (name) {
        case 'Start':
            state.lp=[msg.startLp0,msg.startLp1];
            break;
        case 'NewTurn':
            this.lethal.reset();
            this.repeat.clear();
            if (state.turn) this.guide=null;
            break;
        case 'Move':
            if (this.own(msg.previous.controller) && msg.previous.location === L.extra) {
                const index = this.extra.indexOf(normal(msg.code));
                if (index >= 0) this.extra.splice(index, 1);
            }
            if (this.own(msg.current.controller) && msg.current.location === L.extra && msg.code)
                this.extra.push(normal(msg.code));
            fromTo(msg.previous,(area,index,field)=>{ if (field) delete area[index]; else area.splice(index,1); });
            fromTo(msg.current,(area,index,field,position)=>{
                if (field) area[index]={code:msg.code,position};
                else if (msg.code) area.splice(index,0,msg.code);
            });
            break;
        case 'UpdateData':
            if ([L.monster,L.spell].includes(msg.location)) this.zones[msg.player][msg.location===L.monster?0:1]=msg.cards.slice();
            if (this.own(msg.player) && [L.hand,L.grave].includes(msg.location))
                this[msg.location===L.hand?'hand':'grave']=msg.cards.map(c=>c.code).filter(Boolean);
            break;
        case 'UpdateCard':
            if ([L.monster,L.spell].includes(msg.location)) this.region(msg.controller,msg.location)[msg.sequence]=msg.card;
            break;
        case 'PosChange':
            if (msg.card.location===L.monster) {
                const area=this.region(msg.card.controller,msg.card.location);
                area[msg.card.sequence]={...area[msg.card.sequence],code:msg.code,position:msg.currentPosition};
            }
            break;
        case 'Draw':
            if (this.own(msg.player)) this.hand.push(...msg.cards.map(id=>id&0x7fffffff).filter(Boolean));
            break;
        case 'ShuffleHand':
            if (this.own(msg.player)) this.hand=msg.cards.map(id=>id&0x7fffffff).filter(Boolean);
            break;
        }
        update(msg,name,state,()=>this.view());
        if (name==='NewPhase') this.repeat.clear();
        if (state.hit && this.guide) {
            this.log({event:'amazoness-replan',effect:state.hit,phase:state.phase});
            this.guide=null;
        }
    }

    choose(msg) {
        const name=nameOf(msg), view=this.view(), s=this.state;
        s.declarations=[...new Set(view.hand)];
        if (name==='SelectIdleCmd' && this.firstChoice) {
            this.firstChoice=false;
            const conflicts=[C.chief,C.fighter,C.harpie,C.mirror,C.rescue];
            if (this.seat===0 && s.turn===1 && view.hand.includes(C.curse) &&
                view.hand.some(c=>[C.ash,C.belle,C.ogre,C.rose].includes(c)) &&
                !view.hand.some(c=>conflicts.includes(c))) this.guide=standardChooser();
        }
        try {
            const lethal = this.lethal.choose(msg, name, s, view, this.extra);
            if (lethal) {
                this.log({ event: 'amazoness-lethal-plan', prompt: name });
                return lethal;
            }
            if (name === 'SelectPosition') {
                const [position] = choices(msg, s, view);
                return position ? this.commit(name, position, 'position-rules') : this.safeResponse(msg, name);
            }
            if (this.guide && s.turn===1) {
                const raw=name==='SelectEffectYn' ? choices(msg,s,view) : optionsFor(msg,s);
                const solving=[...s.chains.entries()].find(([,value])=>value===s.solving)?.[0];
                const line=this.guide({msg,state:{...s,solving},visible:{...view,field:view.own.filter(c=>c.location===L.monster)},
                    name,choices:raw});
                if (line) return this.commit(name,line,'standard');
                this.log({ event:'amazoness-guide-diverged', prompt:name, phase:s.phase });
                this.guide=null;
            }
            const options=choices(msg,s,view);
            const allowed=this.preserveLethal(msg,name,options,view);
            if (!allowed.length) return this.safeResponse(msg,name);
            const weights=this.model?.weights;
            let best=-Infinity, selected;
            for (const option of allowed) {
                const seen=this.repeat.get(name+'|'+option.key)??0;
                const score=Math.log(option.weight)+features(option,s,view,name).reduce((n,k)=>n+(weights?.[k]||0),0)
                    - (name==='SelectIdleCmd' ? seen*2 : 0);
                if (score>best) { best=score; selected=option; }
            }
            return this.commit(name,selected,weights?'model':'rules');
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            this.guide=null;
            return this.safeResponse(msg,name);
        }
    }

    preserveLethal(msg,name,options,view) {
        const s=this.state;
        if (name === 'SelectBattleCmd' && !view.enemy.length) {
            const attacks = msg.attackableCards.filter(c=>c.directAttack).map(c=>({
                sequence:c.sequence,
                attack:view.own.find(a=>a.location===L.monster && a.sequence===c.sequence)?.attack ?? 0,
            }));
            const opponentLp=s.lp[1-this.seat];
            if (opponentLp>0 && attacks.reduce((sum,c)=>sum+Math.max(0,c.attack),0)>=opponentLp) {
                const best=attacks.reduce((a,b)=>a.attack>=b.attack?a:b);
                const attack=options.find(o=>o.key.startsWith('attack:') && Number(o.key.split(':')[2])===best.sequence);
                if (attack) return [attack];
            }
        }
        if (s.turn<2 || s.turnPlayer!==this.seat || s.phase!==F.main1 ||
            view.enemy.some(c=>c.location===L.spell || !(c.position&5))) return options;
        const attackers=view.own.filter(c=>c.location===L.monster && (!s.chiefLock || amazon.has(c.code)));
        if (attackProjection(attackers,view.enemy)<s.lp[1-this.seat]) return options;
        if (name==='SelectIdleCmd') {
            const battle=options.find(o=>o.key==='battle');
            if (battle) return [battle];
        }
        if (name==='SelectEffectYn' && msg.code===C.chief && msg.location===L.monster)
            return options.filter(o=>o.key.endsWith(':false'));
        if (name==='SelectChain' && !msg.chains.some(c=>c.forced))
            return options.filter(o=>o.chainLocation!==L.monster || !o.key.startsWith(C.chief+':'));
        return options;
    }

    commit(name,choice,route) {
        if (name==='SelectIdleCmd') {
            this.state.lastIdle=choice.card;
            this.state.hint=undefined;
            const key=name+'|'+choice.key;
            this.repeat.set(key,(this.repeat.get(key)??0)+1);
        }
        if (name==='SelectBattleCmd' && choice.key.startsWith('attack:'))
            this.state.attacker=Number(choice.key.split(':')[2]);
        if (name==='SelectChain' && choice.key!=='pass') this.state.pendingEffect=normal(Number(choice.key.split(':')[0]));
        this.log({event:'amazoness-action',prompt:name,action:choice.key,route});
        return choice.response;
    }

    safeResponse(msg,name) {
        this.log({event:'amazoness-fallback',prompt:name});
        if (name==='SelectIdleCmd') {
            if (msg.canBp) return msg.prepareResponse(P.IdleCmdType.TO_BP);
            if (msg.canEp) return msg.prepareResponse(P.IdleCmdType.TO_EP);
        }
        if (name==='SelectBattleCmd') {
            if (msg.canM2) return msg.prepareResponse(P.BattleCmdType.TO_M2);
            if (msg.canEp) return msg.prepareResponse(P.BattleCmdType.TO_EP);
        }
        return msg.defaultResponse();
    }
}

module.exports={AmazonessPolicy};
