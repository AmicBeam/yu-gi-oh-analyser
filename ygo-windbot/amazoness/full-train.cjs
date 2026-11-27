'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { parseArgs } = require('node:util');
const P = require('ygopro-msg-encode');
const coreAPI = require('koishipro-core.js');
const { nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { loadEngine, optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { C, L, F, amazon, normal, searchPolicy, choices } = require('./duel-policy.cjs');

const MONSTER_QUERY = P.OcgcoreCommonConstants.QUERY_CODE | P.OcgcoreCommonConstants.QUERY_POSITION |
    P.OcgcoreCommonConstants.QUERY_ATTACK | P.OcgcoreCommonConstants.QUERY_DEFENSE |
    P.OcgcoreCommonConstants.QUERY_LEVEL | P.OcgcoreCommonConstants.QUERY_STATUS | P.OcgcoreCommonConstants.QUERY_TYPE |
    P.OcgcoreCommonConstants.QUERY_ATTRIBUTE | P.OcgcoreCommonConstants.QUERY_EQUIP_CARD;
const wireKey = value => Buffer.from(value).toString('base64');
const hash = value => createHash('sha256').update(value).digest('hex');
const read = file => JSON.parse(fs.readFileSync(file,'utf8'));
const write = (file,value) => {
    fs.writeFileSync(file+'.tmp',JSON.stringify(value,null,2)+'\n');
    fs.renameSync(file+'.tmp',file);
};

function randomFrom(seed) {
    return () => ((seed = (Math.imul(seed,1664525) + 1013904223) >>> 0) / 2 ** 32);
}
function shuffle(list,random) {
    const shuffled = [...list];
    for (let i=shuffled.length-1;i>0;i--) {
        const j=Math.floor(random()*(i+1));
        [shuffled[i],shuffled[j]]=[shuffled[j],shuffled[i]];
    }
    return shuffled;
}
function deckFrom(file) {
    const main=[],extra=[];
    let area;
    for (const line of fs.readFileSync(file,'utf8').split(/\r?\n/)) {
        if (line==='#main') area=main;
        else if (line==='#extra') area=extra;
        else if (line==='!side') break;
        else if (/^\d+$/.test(line) && area) area.push(Number(line));
    }
    return {main,extra};
}
function at(duel,player,location) {
    const flag=[L.monster,L.spell].includes(location) ? MONSTER_QUERY : 1;
    return duel.queryFieldCard({player,location,queryFlag:flag,useCache:0}).cards
        .map((c,sequence)=>({...c,sequence,location})).filter(c=>c.code);
}
function visible(duel,bot) {
    const own=[...at(duel,bot,L.monster),...at(duel,bot,L.spell)];
    const enemy=[...at(duel,1-bot,L.monster),...at(duel,1-bot,L.spell)].map(c=>c.position&5 ? c :
        ({ location:c.location, sequence:c.sequence, position:c.position }));
    return { own, enemy, hand:at(duel,bot,L.hand).map(c=>normal(c.code)), grave:at(duel,bot,L.grave).map(c=>normal(c.code)) };
}

function createDuel(core,input) {
    const duel=core.createDuelV2([input.seed,0,0,0,0,0,0,0]);
    for (const script of ['./script/patches/entry.lua','./script/special.lua','./script/init.lua'])
        duel.preloadScript(script);
    const sides=input.bot===0 ? [input,input.opponent] : [input.opponent,input];
    const lp=input.bot===0 ? [input.ourLP,input.enemyLP] : [input.enemyLP,input.ourLP];
    sides.forEach((side,player)=>{
        duel.setPlayerInfo({player,lp:lp[player],startHand:5,drawCount:1});
        for (const [cards,location] of [[side.main,L.deck],[side.extra,L.extra]]) {
            for (const code of [...cards].reverse())
                duel.newCard({code,owner:player,player,location,sequence:0,position:8});
        }
    });
    for (const card of input.setup) {
        const player=card.our ? input.bot : 1-input.bot;
        duel.newCard({ code:card.code,owner:player,player,location:card.location,
            sequence:card.sequence,position:card.position });
    }
    duel.startDuel((5<<16) | coreAPI.OcgcoreDuelOptionFlag.PseudoShuffle);
    return duel;
}

function update(msg,name,state,getView) {
    const bot=state.bot, enemy=1-bot;
    if (name==='NewTurn') {
        state.turn++; state.turnPlayer=msg.player; state.phase=0;
        state.pending=0; state.enemyPending=0; state.enemySummons=0; state.maxxUsed=false; state.drollUsed=false;
        state.chiefLock=false; state.calledTarget=false; state.swordNegateUsed=false; state.battle=null;
        state.georgeNegateUsed=false;
    }
    if (name==='NewPhase') {
        state.phase=msg.phase;
        if (msg.phase===F.main1 && state.turnPlayer===bot) state.startOppCount=getView().enemy.length;
        if (msg.phase===F.main2 && state.turnPlayer===bot) {
            state.main2=true;
            state.clearedBeforeMain2=state.startOppCount-getView().enemy.length;
        }
    }
    if (name==='Damage') { state.lp[msg.player]-=msg.value; if (msg.player===enemy && state.turnPlayer===bot) state.damage+=msg.value; }
    if (name==='Recover') state.lp[msg.player]+=msg.value;
    if (name==='PayLpCost') state.lp[msg.player]-=msg.cost;
    if (name==='Chaining') {
        const link={code:normal(msg.code),player:msg.controller,location:msg.location,sequence:msg.sequence,desc:msg.desc};
        state.chains.set(msg.chainCount,link);
        if (link.player===bot) {
            if (link.code===C.sword && link.desc===C.sword*16+2) state.swordNegateUsed=true;
            if (link.code===C.george && link.desc===C.george*16+1) state.georgeNegateUsed=true;
        }
        state.trace.push([name,msg.code,msg.controller,msg.location,msg.desc,msg.chainCount,state.turn,state.phase]);
    }
    if (name==='ChainSolving') state.solving=state.chains.get(msg.chainCount);
    if (name==='ChainDisabled'||name==='ChainNegated') {
        const link=state.chains.get(msg.chainCount);
        if (link?.player===enemy) state.interactions++;
        if (link) link.denied=true;
        state.trace.push([name,msg.chainCount,link?.code]);
    }
    if (name==='ChainSolved') {
        const link=state.chains.get(msg.chainCount);
        if (link?.player===bot && !link.denied) {
            if (link.code===C.baronne && link.desc===C.baronne*16+1) state.spentBaronne.add(link.sequence);
            if (link.code===C.chief && link.location===L.monster) state.chiefLock=true;
            if (link.code===C.call && link.location===L.grave) state.calledTarget=true;
            if (link.code===C.maxx) state.maxxUsed=true;
            if (link.code===C.droll) state.drollUsed=true;
            if (link.code===C.saryuja) state.draw+=state.handOrigins.filter(entry=>entry.source===link).length*50;
        } else if (link?.player===enemy && !link.denied) state.hit=link.code;
        state.solving=null;
    }
    if (name==='ChainEnd') { state.chains.clear(); state.lastTargeted=null; state.pendingEffect=null; }
    if (name==='Draw' && msg.player===bot) {
        state.handOrigins.push(...msg.cards.map(code=>({code:code&0x7fffffff,source:state.solving})));
        if (state.turn && state.solving?.player===bot && ![C.maxx,C.saryuja].includes(state.solving.code)) {
            state.draw+=state.solving.code===C.dugares && msg.count===2 ? 75 : msg.count*50;
            state.trace.push([name,state.solving.code,msg.count]);
        }
    }
    if (name==='ShuffleHand' && msg.player===bot) {
        const previous=[...state.handOrigins];
        state.handOrigins=msg.cards.map(code=>{
            const index=previous.findIndex(entry=>entry.code===code);
            return index<0?{code}:previous.splice(index,1)[0];
        });
    }
    if (name==='Draw' && state.turn && state.turnPlayer===bot && msg.player===enemy && msg.count && state.phase!==1)
        state.enemyDraws+=msg.count;
    if (name==='BecomeTarget') {
        const target=msg.targets.find(value=>(value&255)===bot && (value>>>8&255)===L.monster);
        if (target!==undefined) state.lastTargeted=target>>>16&255;
    }
    if (name==='Summoning'||name==='SpSummoning') {
        if (msg.controller===enemy) state.enemyPending++;
    }
    if (name==='Summoned'||name==='SpSummoned') {
        state.enemySummons+=state.enemyPending; state.enemyPending=0;
    }
    if (name==='Attack') {
        const snapshot=getView();
        const attacker=msg.attacker, target=msg.defender;
        const position=(owner,sequence)=>[...snapshot.own,...snapshot.enemy].find(c=>
            c.location===L.monster && c.sequence===sequence && (snapshot.own.includes(c)?owner===bot:owner===enemy));
        const a=position(attacker?.controller,attacker?.sequence), t=position(target?.controller,target?.sequence);
        const ours=attacker?.controller===bot ? a : t, theirs=attacker?.controller===bot ? t : a;
        const power=card=>card?.position&4 ? card.defense||0 : card?.attack||0;
        state.battle={ours:power(ours),theirs:power(theirs),damage:Math.max(0,power(theirs)-power(ours))};
        state.trace.push([name,attacker?.controller,attacker?.sequence,target?.sequence,state.turn]);
    }
    if (name==='Hint' && msg.player===bot && msg.type===3) state.hint=msg.desc;
    if (name==='Move') {
        const {previous,current}=msg;
        const [origin]=previous.controller===bot && previous.location===L.hand ? state.handOrigins.splice(previous.sequence,1):[];
        if (current.controller===bot && current.location===L.hand) state.handOrigins.splice(current.sequence,0,origin||{code:msg.code});
        if (previous.controller===bot && previous.location===L.monster && msg.code===C.baronne) {
            const spent=state.spentBaronne.delete(previous.sequence);
            if (spent && current.controller===bot && current.location===L.monster) state.spentBaronne.add(current.sequence);
        }
        if (state.solving?.player===bot && previous.controller===enemy && [L.monster,L.spell].includes(previous.location) &&
            (current.controller!==enemy || ![L.monster,L.spell].includes(current.location))) {
            if (state.turnPlayer===enemy) state.interactions++;
            state.trace.push(['EffectClear',state.solving.code,msg.code,state.turn]);
        }
        if ([L.monster,L.spell].includes(current.location)||[L.monster,L.spell].includes(previous.location))
            state.trace.push([name,msg.code,previous.controller,previous.location,current.controller,current.location,state.turn]);
    }
}

function boardScore(view,state) {
    const monsters=view.own.filter(c=>c.location===L.monster);
    const spells=view.own.filter(c=>c.location===L.spell);
    const enabled=monsters.filter(c=>(c.position&5) && !(c.status & 1));
    const george=enabled.find(c=>c.code===C.george);
    const equipped=george && spells.filter(c=>c.equipCard?.controller===state.bot && c.equipCard.sequence===george.sequence);
    const bodies=enabled.filter(c=>amazon.has(normal(c.code)));
    return {
        sword:enabled.some(c=>c.code===C.sword)?100:0,
        baronne:enabled.some(c=>c.code===C.baronne && !state.spentBaronne.has(c.sequence))?150:0,
        georgeSpell:equipped?.length?100:0,
        georgeLock:equipped?.some(c=>c.code===C.legion)?100:0,
        rescue:spells.some(c=>c.code===C.rescue) && monsters.filter(c=>c.sequence<5).length<5 &&
            ((state.canRescue??true)||view.hand.some(c=>amazon.has(c)))?100:0,
        formation:spells.some(c=>c.code===C.formation)&&bodies.length&&view.grave.some(c=>amazon.has(c))?100:0,
        draw:state.draw,
    };
}

function opponent(msg,name,state,input) {
    const canPlay=state.turnPlayer!==state.bot && (input.bot===0 || state.turn>=3);
    if (name==='SelectIdleCmd') {
        const I=P.IdleCmdType;
        const summon=msg.summonableCards.find(c=>input.pressure.includes(c.code));
        if (canPlay && summon) return msg.prepareResponse(I.SUMMON,summon);
        const spell=msg.activatableCards.find(c=>input.pressure.includes(c.code));
        if (canPlay && spell) return msg.prepareResponse(I.ACTIVATE,spell);
        return msg.prepareResponse(canPlay && msg.canBp ? I.TO_BP : I.TO_EP);
    }
    if (name==='SelectBattleCmd') {
        const B=P.BattleCmdType;
        return msg.attackableCards.length ? msg.prepareResponse(B.ATTACK,P.IndexResponse(0)) :
            msg.prepareResponse(msg.canM2 ? B.TO_M2 : B.TO_EP);
    }
    if (name==='SelectChain') {
        const prior=[...state.chains.values()].at(-1);
        const card=msg.chains.find(c=>c.forced)||msg.chains.find(c=>input.pressure.includes(c.code) &&
            !state.opponentUsed.has(c.code) && (canPlay || prior?.player===state.bot || (c.location===L.spell && state.phase>=8)));
        if (card) state.opponentUsed.add(card.code);
        return msg.prepareResponse(card||undefined);
    }
    if (name==='SelectEffectYn') return msg.prepareResponse(canPlay && input.pressure.includes(msg.code));
    if (name==='SelectYesNo') return msg.prepareResponse(false);
    if (name==='SelectCard') return msg.prepareResponse([...msg.cards].sort((a,b)=>
        Number(b.controller===state.bot)-Number(a.controller===state.bot)).slice(0,msg.min));
    if (name==='SelectUnselectCard') return msg.prepareResponse(msg.finishable?null:P.IndexResponse(0));
    if (name==='SelectPlace') return msg.prepareResponse(msg.getSelectablePlaces().slice(0,msg.count||1));
    if (name==='SelectPosition') return msg.prepareResponse(msg.positions & -msg.positions);
    if (name==='SelectOption') return msg.prepareResponse(P.IndexResponse(0));
    return msg.defaultResponse();
}

function features(option,state,view,name) {
    const key=option.key;
    const ours=view.own.filter(c=>c.location===L.monster);
    const theirs=view.enemy.filter(c=>c.location===L.monster);
    return [`a:${name}:${key}`,`phase:${state.phase}:${key}`,`bodies:${ours.length}:${key}`,
        `enemy:${theirs.length}:${key}`,`hit:${!!state.hit}:${key}`,`own-turn:${state.turnPlayer===state.bot}:${key}`,
        `their-lp:${Math.ceil(state.lp[1-state.bot]/1000)}:${key}`];
}
function choose(options,random,model) {
    if (model || !random)
        return options.reduce((best,o)=>o.rank>best.rank?o:best);
    let n=random()*options.reduce((sum,o)=>sum+o.weight,0);
    return options.find(o=>(n-=o.weight)<0)||options.at(-1);
}

function rollout(engine,input,mode) {
    const duel=createDuel(engine.core,input);
    let ourEndboard;
    const state={bot:input.bot,cardReader:engine.cardReader,turn:0,phase:0,chains:new Map(),spentBaronne:new Set(),opponentUsed:new Set(),enemySummons:0,
        draw:0,enemyDraws:0,damage:0,lp:input.bot===0?[input.ourLP,input.enemyLP]:[input.enemyLP,input.ourLP],
        startOppCount:0,interactions:0,trace:[],handOrigins:[],declarations:[...new Set(input.main)],searchPolicy};
    const wire=[],steps=[];
    const finish=won=>{
        state.canRescue=at(duel,state.bot,L.deck).some(c=>amazon.has(normal(c.code)));
        const view=visible(duel,state.bot);
        const breakdown=boardScore(view,state);
        const points=Object.values(breakdown).reduce((a,b)=>a+b,0)-50*state.enemyDraws;
        const outcome=input.bot===1 ? [won,state.clearedBeforeMain2??state.startOppCount-view.enemy.length,state.damage,points] :
            [won,state.interactions,Math.max(0,state.lp[state.bot]),ourEndboard?.points??points,points];
        return { outcome, main2:!!state.main2, turn:state.turn, beforeOpp:ourEndboard, board:view.own.map(c=>c.code),
            breakdown, redraws:state.enemyDraws, interactions:state.interactions, hit:state.hit||0, trace:state.trace, steps, wire };
    };
    try {
        for (let n=0;n<1200 && Date.now()<mode.deadline;n++) {
            const result=duel.process();
            for (const msg of result.messages||[]) {
                const name=nameOf(msg);
                if (name==='Retry') return {error:'Retry'};
                update(msg,name,state,()=>visible(duel,state.bot));
                if (name==='NewTurn' && state.turn===2 && input.bot===0) {
                    state.canRescue=at(duel,state.bot,L.deck).some(c=>amazon.has(normal(c.code)));
                    const snapshot=visible(duel,state.bot), items=boardScore(snapshot,state);
                    ourEndboard={items,points:Object.values(items).reduce((a,b)=>a+b,0)-50*state.enemyDraws,
                        board:snapshot.own.map(c=>c.code)};
                }
                if (name==='NewTurn' && state.turn===3) return finish(0);
                if (name==='Win') return finish(msg.player===state.bot?1:msg.player===1-state.bot?-1:0);
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                const offset=wire.length, old=mode.prefix?.[offset];
                if (mode.replay && old===undefined) return { error:'ShortReplay' };
                let response;
                if (msg.responsePlayer()!==state.bot) {
                    response=opponent(msg,name,state,input);
                    if (old!==undefined && offset<(mode.pivot??Infinity) && wireKey(response)!==old) return {error:'OpponentDiverged'};
                } else {
                    const view=visible(duel,state.bot);
                    let options=mode.replay && name==='SelectPosition' ? optionsFor(msg,state) : choices(msg,state,view);
                    if (!options.length) return {error:'NoPolicyAction:'+name};
                    options=options.map(o=>{
                        const keys=features(o,state,view,name);
                        return {...o,keys,rank:Math.log(o.weight)+keys.reduce((n,k)=>n+(mode.model?.[k]||0),0)};
                    });
                    let choice;
                    if (old!==undefined && offset<(mode.pivot??Infinity)) choice=options.find(o=>wireKey(o.response)===old);
                    else if (old!==undefined && offset===mode.pivot) {
                        const alternatives=options.filter(o=>wireKey(o.response)!==old);
                        choice=choose(alternatives.length?alternatives:options,mode.random,mode.model);
                    } else choice=choose(options,mode.random,mode.model);
                    if (!choice) return {error:'PolicyDiverged'};
                    response=choice.response;
                    if (options.length>1) steps.push({offset,prefix:hash(wire.join('|')).slice(0,20),
                        action:wireKey(response),features:choice.keys,name,key:choice.key});
                    if (name==='SelectIdleCmd') { state.lastIdle=choice.card; state.hint=undefined; }
                    if (name==='SelectChain' && choice.key!=='pass') state.pendingEffect=normal(Number(choice.key.split(':')[0]));
                    if (name==='SelectBattleCmd' && choice.key.startsWith('attack:'))
                        state.attacker=Number(choice.key.split(':')[2]);
                }
                if (!response) return {error:'NoEngineResponse:'+name};
                wire.push(wireKey(response));
                duel.setResponse(response);
            }
        }
        return {error:'Budget'};
    } catch (error) {
        if (error instanceof UnsupportedPrompt) return {error:'Unsupported:'+error.prompt};
        throw error;
    } finally {
        duel.endDuel();
    }
}

function verified(engine,input,result) {
    const duplicate=result?.outcome && rollout(engine,input,{prefix:result.wire,replay:true,deadline:Date.now()+2000});
    return Boolean(duplicate?.outcome && duplicate.main2===result.main2 &&
        duplicate.wire.join('|')===result.wire.join('|') && duplicate.outcome.join(',')===result.outcome.join(','));
}

function compare(a, b) {
    if (a[0] === 1 && b[0] === 1) return 0;
    for (let axis = 0; axis < a.length; axis++) {
        if (a[axis] !== b[axis]) return a[axis] - b[axis];
    }
    return 0;
}

function buildPairs(runs) {
    const byPrefix = new Map();
    for (const run of runs) {
        for (const step of run.steps) {
            if (!byPrefix.has(step.prefix)) byPrefix.set(step.prefix, new Map());
            const actions = byPrefix.get(step.prefix);
            const previous = actions.get(step.action);
            if (!previous || compare(run.outcome, previous.outcome) > 0)
                actions.set(step.action, { features:step.features, outcome:run.outcome });
        }
    }
    const pairs = [];
    for (const actions of byPrefix.values()) {
        const [best, ...alternatives] = [...actions.values()].sort((a,b)=>compare(b.outcome,a.outcome));
        for (const other of alternatives) {
            const axis = best.outcome.findIndex((value,i)=>value!==other.outcome[i]);
            if (axis >= 0 && compare(best.outcome, other.outcome) > 0 &&
                best.features.join('|') !== other.features.join('|')) {
                pairs.push({ high:best.features, low:other.features, axis });
            }
        }
    }
    return pairs;
}

function search(engine,input,{attempts,caseMs}) {
    const random=randomFrom(input.seed), deadline=Date.now()+caseMs;
    const lines=[], seen=new Set(), errors={};
    for (let i=0;i<attempts&&Date.now()<deadline;i++) {
        const parents=lines.slice(0,14).filter(a=>a.steps.length);
        const parent=i%3 ? parents[Math.floor(random()*parents.length)] : undefined;
        const prefix=parent?.wire, pivot=parent?.steps[Math.floor(random()*parent.steps.length)]?.offset;
        const result=rollout(engine,input,{random:i?random:undefined,prefix,pivot,deadline});
        if (!result.outcome) { errors[result.error]=(errors[result.error]||0)+1; continue; }
        const key=hash(result.wire.join('|'));
        if (seen.has(key)) continue;
        seen.add(key);
        lines.push(result);
        lines.sort((a,b)=>compare(b.outcome,a.outcome));
    }
    const eligible=lines.slice(0,12).filter(result=>verified(engine,input,result));
    return { settled:lines.length, replayed:eligible.length, errors, best:eligible[0],
        pairs:buildPairs(eligible) };
}

function fit(rows,seed) {
    const random=randomFrom(seed), model={};
    for (let epoch=0;epoch<6;epoch++) {
        for (const {high,low,axis} of shuffle(rows,random)) {
            const left=high.filter(k=>!low.includes(k)), right=low.filter(k=>!high.includes(k));
            const margin=left.reduce((n,k)=>n+(model[k]||0),0)-right.reduce((n,k)=>n+(model[k]||0),0);
            const amount=(axis===0?0.18:0.08)/(1+Math.exp(margin));
            for (const [keys,sign] of [[left,1],[right,-1]]) for (const k of keys)
                model[k]=Math.max(-1.25,Math.min(1.25,(model[k]||0)+sign*amount));
        }
    }
    return model;
}

const m=(code,sequence=0,position=1,our=false)=>({code,sequence,position,our,location:L.monster});
const s=(code,sequence=0,our=false)=>({code,sequence,position:8,our,location:L.spell});
const g=(code,sequence=0)=>({code,sequence,position:1,our:true,location:L.grave});
const filler=89631139, knight=28985331, lady=7572887, solemn=41420027, mirrorForce=44095762, raigeki=12580477;

function makeCases(deck,count,holdout,seed) {
    const random=randomFrom(seed), seen=new Set(), result=[];
    const layouts=[
        {cards:[],lp:3600,pressure:[]},
        {cards:[m(lady)],lp:3400,pressure:[]},
        {cards:[m(filler,0,4)],lp:8000,pressure:[C.imperm]},
        {cards:[m(filler),s(mirrorForce)],lp:6300,pressure:[mirrorForce]},
        {cards:[m(lady),m(filler,1,4)],lp:8000,pressure:[C.ash]},
        {cards:[m(filler,0,8),s(solemn)],lp:5900,pressure:[solemn]},
        {cards:[m(lady,0,4),m(lady,1,4)],lp:8000,pressure:[C.nibiru]},
        {cards:[m(filler)],lp:1100,pressure:[C.droll]},
    ];
    for (let i=0;i<count+holdout;i++) {
        const bot=i%3===0?0:1;
        const layout=layouts[i%layouts.length];
        let main,key;
        do {
            main=shuffle(deck.main,random);
            key=main.slice(0,bot?6:5).sort((a,b)=>a-b).join(',');
        } while(seen.has(key));
        seen.add(key);
        const pressure=bot?layout.pressure:[knight,raigeki,C.ash].slice(0,1+i%3);
        const threats=Array.from(new Set([...pressure,74677422]));
        result.push({id:i+1,bot,main,extra:deck.extra,seed:Math.floor(random()*2**32),
            setup:bot?layout.cards:[],ourLP:8000,enemyLP:bot?layout.lp:8000,pressure,
            opponent:{main:[...threats,...Array(39-threats.length).fill(filler),46986414],extra:[]} });
    }
    return {train:result.slice(0,count),holdout:result.slice(count)};
}

function directedCases(deck) {
    const make=(id,bot,setup,enemyLP,pressure,head=[])=>{
        const rest=[...deck.main];
        for (const code of head) rest.splice(rest.indexOf(code),1);
        return {id,bot,setup,enemyLP,ourLP:8000,pressure,seed:1752149,main:[...head,...rest],extra:deck.extra,
            opponent:{main:[...pressure,74677422,...Array(38-pressure.length).fill(filler),46986414],extra:[]}};
    };
    const blank=[C.nibiru,C.nibiru,C.ash,C.belle,C.imperm];
    return [
        make('direct-lethal',1,[m(C.sword,0,1,true),m(C.chief,1,1,true)],3500,[],blank),
        make('clear-and-main2',1,[m(C.sword,0,1,true),m(lady,0,4)],8000,[],[...blank,C.rescue]),
        make('fusion-empress',1,[m(C.princess,0,1,true),m(C.curse,1,1,true),m(C.sword,2,1,true)],6300,[],blank),
        make('grave-empress',1,[m(C.dragon,0,1,true),g(C.princess),g(C.sword,1)],6300,[],blank),
        make('curse-harpie',1,[m(C.curse,0,1,true),m(C.fighter,1,1,true),s(C.formation,4,true)],6000,[],
            [C.harpie,C.nibiru,C.ash,C.belle,C.imperm]),
        make('rescue-recovery',0,[m(C.sword,0,1,true),g(C.rescue),g(C.fighter,1)],8000,[knight],blank),
        make('formation-removal',0,[m(C.sword,0,1,true),g(C.fighter),s(C.formation,0,true),s(C.rescue,1,true)],8000,[lady],blank),
        make('sword-self',0,[m(C.sword,0,1,true)],8000,[knight],blank),
        make('raigeki-pressure',0,[m(C.sword,0,1,true),m(C.baronne,1,1,true),s(C.rescue,0,true)],8000,[raigeki,knight],blank),
    ];
}

function config() {
    const {values}=parseArgs({options:{fixture:{type:'string'},data:{type:'string',multiple:true},deck:{type:'string'},
        output:{type:'string'},seed:{type:'string',default:'20261128'},cases:{type:'string',default:'45'},
        holdout:{type:'string',default:'18'},attempts:{type:'string',default:'100'},
        'case-ms':{type:'string',default:'1700'}}});
    for (const key of ['fixture','data','deck','output']) if (!values[key]) throw new Error('Missing --'+key);
    for (const key of ['seed','cases','holdout','attempts','case-ms']) {
        values[key]=Number(values[key]);
        if (!Number.isSafeInteger(values[key])||values[key]<1) throw new Error('Invalid --'+key);
    }
    return {...values,caseMs:values['case-ms']};
}

async function main() {
    const opts=config(), deck=deckFrom(opts.deck), engine=await loadEngine(path.resolve(opts.fixture),opts.data.map(p=>path.resolve(p)));
    const output=path.resolve(opts.output);
    fs.mkdirSync(output,{recursive:true});
    const at=name=>path.join(output,name);
    const cases=makeCases(deck,opts.cases,opts.holdout,opts.seed), directed=directedCases(deck);
    const scripts={};
    for (const id of new Set([...deck.main,...deck.extra,...[...cases.train,...cases.holdout,...directed]
        .flatMap(c=>[...c.setup.map(q=>q.code),...c.opponent.main])])) {
        const card=engine.cardReader(id);
        if (!card) throw new Error('Missing card: '+id);
        if (card.type&16) continue;
        const script=[card.ruleCode,card.alias,id].filter(Boolean).map(code=>engine.scriptReader('script/c'+code+'.lua')).find(Boolean);
        if (!script) throw new Error('Missing card script: '+id);
        scripts[id]=hash(script);
    }
    const inputs={...cases,directed,deckSha256:hash(fs.readFileSync(opts.deck)), settings:opts,scripts,
        trainerSha256:hash(fs.readFileSync(__filename)),policySha256:hash(fs.readFileSync(require.resolve('./duel-policy.cjs')))};
    if (fs.existsSync(at('inputs.json'))) {
        if (JSON.stringify(read(at('inputs.json')))!==JSON.stringify(inputs)) throw new Error('Training configuration changed');
    } else write(at('inputs.json'),inputs);
    const results=[];
    for (const input of cases.train) {
        const file=at('case-'+input.id+'.json');
        const r=fs.existsSync(file)?read(file):search(engine,input,opts);
        if (engine.engineErrors.size) throw new Error([...engine.engineErrors].slice(0,6).join('\n'));
        if (!fs.existsSync(file)) write(file,r);
        results.push(r);
        console.log(JSON.stringify({id:input.id,bot:input.bot,settled:r.settled,replayed:r.replayed,
            outcome:r.best?.outcome,pairs:r.pairs.length,errors:r.errors}));
    }
    const weights={};
    for (const bot of [0,1]) weights[bot]=fit(results.filter((_,i)=>cases.train[i].bot===bot).flatMap(r=>r.pairs),opts.seed+bot);
    write(at('candidate.json'),{version:1,objective:'amazoness-complete-duel-v1',deckSha256:inputs.deckSha256,
        trainerSha256:inputs.trainerSha256,policySha256:inputs.policySha256,weights});
    const report=[];
    for (const input of [...cases.holdout,...directed]) {
        const rule=rollout(engine,input,{deadline:Date.now()+opts.caseMs});
        const candidate=rollout(engine,input,{model:weights[input.bot],deadline:Date.now()+opts.caseMs});
        const reference=search(engine,input,opts);
        if (engine.engineErrors.size) throw new Error([...engine.engineErrors].slice(0,6).join('\n'));
        const row={id:input.id,bot:input.bot,rule:rule.outcome,candidate:candidate.outcome,reference:reference.best?.outcome,
            candidateMain2:candidate.main2,referenceMain2:reference.best?.main2,
            ruleVerified:verified(engine,input,rule),candidateVerified:verified(engine,input,candidate),
            referenceVerified:reference.replayed>0,ruleError:rule.error,candidateError:candidate.error};
        write(at('holdout-'+input.id+'.json'),{rule,candidate,reference:reference.best,errors:reference.errors});
        report.push(row);
    }
    const summary={training:{hands:results.length,settled:results.reduce((n,r)=>n+r.settled,0),
        replayed:results.reduce((n,r)=>n+r.replayed,0),pairs:results.reduce((n,r)=>n+r.pairs.length,0)},
        holdout:report.filter(r=>typeof r.id==='number'),directed:report.filter(r=>typeof r.id!=='number')};
    write(at('summary.json'),summary);
    console.log(JSON.stringify(summary));
}

if (require.main===module) main().catch(error=>{console.error(error.stack);process.exitCode=1;});
module.exports={rollout,verified,deckFrom,makeCases,directedCases,update,features,compare};
