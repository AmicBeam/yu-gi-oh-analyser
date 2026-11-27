'use strict';

const P = require('ygopro-msg-encode');
const { OcgcoreDuelOptionFlag } = require('koishipro-core.js');
const { spawnSync } = require('node:child_process');
const { engineOptions, nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { twinsPolicy, position } = require('../tests/twins-engine-reference.cjs');
const { stateFeatures, features: twinsFeatures } = require('./second-turn-search.cjs');
const { MagistusPolicy } = require('../magistus/live-policy.cjs');
const { features: magistusFeatures, battleOptions } = require('../magistus/second-turn.cjs');
const { context } = require('../magistus/offline.cjs');
const { AmazonessPolicy } = require('../amazoness/live-policy.cjs');
const { C, choices: amazonChoices } = require('../amazoness/duel-policy.cjs');
const { boardScore, features: amazonFeatures } = require('../amazoness/full-train.cjs');
const { card, trap, settledScore } = require('../../skills/ygo-deck-analyze/scripts/handrip-profile.cjs');
const { optionsFor } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { twinsTie, amazonessTie, magistusTie } = require('../turn-evidence.cjs');
const { compareTurn } = require('../turn-objective.cjs');
const { openWindBot } = require('../tests/replay-windbot-seat.cjs');

const I = P.IdleCmdType, B = P.BattleCmdType;
const PRESSURE = new Set([14558128,10045474,27204311,42141493,23434538,97268402,73642296,94145022]);
const quiet = () => {};
const encoded = response => Buffer.from(response).toString('base64');
const at = (duel, player, location, flag = 0xfbdfff) =>
    duel.queryFieldCard({ player, location, queryFlag: flag, useCache: 0 }).cards;
const snapshot = (duel, player) => [4,8].flatMap(location => at(duel,player,location)
    .flatMap((c,sequence)=>c.code?[{...c,location,sequence}]:[]));

function open(core, scene) {
    const duel = core.createDuelV2([scene.seed,0,0,0,0,0,0,0]);
    duel.loadRegistry({ duel_mode:'single',start_lp:'8000',start_hand:'5',draw_count:'1' });
    for (const script of ['./script/patches/entry.lua','./script/special.lua','./script/init.lua']) duel.preloadScript(script);
    scene.decks.forEach((deck,player) => {
        duel.setPlayerInfo({ player,lp:scene.lp[player],startHand:5,drawCount:1 });
        for (const [cards,location] of [[deck.main,1],[deck.extra,64]])
            for (const code of [...cards].reverse())
                duel.newCard({ code,owner:player,player,location,sequence:0,position:8 });
    });
    for (const c of scene.field) duel.newCard({ ...c,owner:c.player });
    duel.startDuel((5<<16) | OcgcoreDuelOptionFlag.PseudoShuffle);
    return duel;
}

function refresh(duel, seat, observe, extra) {
    for (const player of [0,1]) for (const location of [4,8,...(player===seat?[2,16,...(extra?[64]:[])]:[])]) {
        const cards = at(duel,player,location).map(c=>player===seat || c.empty || c.position&5 ?
            c : { empty:false,position:c.position,code:0 });
        observe(new P.YGOProMsgUpdateData().fromPartial({ player,location,cards }));
    }
}

function prepare(engine, scene, model) {
    const bot = scene.bot, extra = scene.decks[bot].extra;
    const mag = scene.deck==='Magistus', amazon = scene.deck==='Amazoness';
    const policy = amazon ? new AmazonessPolicy(bot,engine.cardReader,model,quiet,extra) :
        mag ? new MagistusPolicy(bot,engine.cardReader,require('../magistus/live-guides.json'),model,quiet,extra) : null;
    const s = { bot,cardReader:engine.cardReader,turn:0,phase:0,field:new Map(), normal:new Set(), grave:[],
        pending:0,summons:0,normalUsed:false,trace:[],interrupted:0,scoringDraws:0,drawsSent:0,damage:0,
        startOpp:null,main2:false,denied:0,chains:new Map(),tried:new Set(),lp:[...scene.lp],
        declarations:[...new Set(scene.decks[bot].main)] };
    if (mag) policy.lp=[...scene.lp];
    if (amazon) policy.state.lp=[...scene.lp];
    return { s,policy,mag,amazon };
}

function observe(duel, scene, runtime, msg) {
    const { s,policy }=runtime, { bot,turn }=scene, n=nameOf(msg);
    policy?.observe(msg.playerView?.(bot) ?? msg);
    if (n==='NewTurn') { s.turn++;s.phase=0;s.summons=0;s.normalUsed=false; }
    if (n==='NewPhase') {
        s.phase=msg.phase;
        if (s.turn===turn && msg.phase===4) s.startOpp=snapshot(duel,1-bot).length;
        if (s.turn===turn && msg.phase===256) s.main2=true;
    }
    if (n==='Damage') {
        s.lp[msg.player]-=msg.value;
        if (s.turn===turn && msg.player!==bot) s.damage+=msg.value;
    }
    if (n==='PayLpCost') s.lp[msg.player]-=msg.cost;
    if (n==='Recover') s.lp[msg.player]+=msg.value;
    if (n==='Chaining') {
        s.chains.set(msg.chainCount,{code:msg.code,player:msg.controller});
        s.trace.push([n,msg.code,msg.controller===bot?0:1,msg.location,msg.desc]);
    }
    if (n==='ChainSolving') s.solving=s.chains.get(msg.chainCount);
    if (n==='ChainSolved') {
        const chain=s.chains.get(msg.chainCount);
        if (chain?.player!==bot && PRESSURE.has(chain?.code)) { s.interrupted++;s.searchLocked ||= chain.code===94145022; }
        s.solving=undefined;
    }
    if (n==='ChainNegated'||n==='ChainDisabled') {
        if (s.chains.get(msg.chainCount)?.player===1-bot) s.denied++;
        s.chains.delete(msg.chainCount);
    }
    if (n==='ChainEnd') s.chains.clear();
    if (n==='Hint' && msg.type===3 && msg.player===bot) s.selectionHint=msg.desc;
    if (n==='Move') {
        const {previous:a,current:b}=msg, code=twinsPolicy.normalize(msg.code);
        if (a.controller===bot && a.location===4) { s.field.delete(a.sequence);s.normal.delete(a.sequence); }
        if (b.controller===bot && b.location===4) s.field.set(b.sequence,code);
        if (a.controller===bot && a.location===16 && s.grave.includes(code)) s.grave.splice(s.grave.indexOf(code),1);
        if (b.controller===bot && b.location===16) s.grave.push(code);
    }
    if (n==='Summoning' && msg.controller===bot) { s.normal.add(msg.sequence);s.normalUsed=true; }
    if (['Summoning','SpSummoning'].includes(n) && msg.controller===bot) s.pending++;
    if (['Summoned','SpSummoned'].includes(n)) { s.summons+=s.pending;s.pending=0; }
    if (n==='Draw' && s.turn===turn && msg.player!==bot) s.drawsSent+=msg.count;
    if (n==='Draw' && msg.player===bot && s.solving?.player===bot &&
        [9205573,9205574,54257392,65330383].includes(s.solving?.code)) s.scoringDraws+=msg.count;
}

function opponent(msg, scene, s) {
    const name=nameOf(msg), seat=msg.responsePlayer(), active=s.turn>=scene.turn;
    if (name==='SelectIdleCmd') return msg.prepareResponse(I.TO_EP);
    if (name==='SelectChain') {
        const botAction=[...s.chains.values()].at(-1)?.player===scene.bot;
        const chain=msg.chains.find(c=>c.forced) ?? (active&&seat!==scene.bot ? msg.chains.find(c=>
            !s.tried.has(c.code) && (PRESSURE.has(c.code) &&
                (c.code===27204311?s.summons>=5:botAction||[23434538,42141493,94145022].includes(c.code)) ||
                c.location===8 && (botAction||[8,16,32,64,128].includes(s.phase)))) : undefined);
        if (chain) s.tried.add(chain.code);
        return msg.prepareResponse(chain);
    }
    if (name==='SelectCard') {
        const cards=[...msg.cards].sort((a,b)=>Number(b.controller!==seat)-Number(a.controller!==seat));
        return msg.prepareResponse(cards.slice(0,msg.min).map(c=>P.IndexResponse(msg.cards.indexOf(c))));
    }
    if (name==='SelectEffectYn') return msg.prepareResponse(active&&seat!==scene.bot);
    if (name==='SelectYesNo') return msg.prepareResponse(false);
    if (name==='SelectPlace'||name==='SelectDisField') return msg.prepareResponse(msg.getSelectablePlaces().slice(0,msg.count||1));
    if (name==='SelectPosition') return msg.prepareResponse(msg.positions & -msg.positions);
    if (name==='SelectBattleCmd') return msg.prepareResponse(msg.canM2?B.TO_M2:B.TO_EP);
    return msg.defaultResponse();
}

function choices(duel, scene, runtime, msg, model) {
    const {s,policy,amazon,mag}=runtime, name=nameOf(msg);
    if (amazon) {
        const view=policy.view(), list=amazonChoices(msg,policy.state,view);
        return list.map(c=>({...c,keys:amazonFeatures(c,policy.state,view,name)}));
    }
    if (mag) {
        policy.refresh();
        const list=name==='SelectBattleCmd'?battleOptions(msg,policy):optionsFor(msg,policy.state)
            .map(c=>({...c,key:policy.key(name,c.key)}));
        const tags=context(policy.state);
        return list.map(c=>({...c,keys:[name+':'+c.key,...tags.map(t=>name+':'+c.key+'|'+t),
            ...magistusFeatures(policy,name,c,model.tactics?.training==='rule-continuation').map(k=>'tactics/'+k)]}));
    }
    let list;
    if (name==='SelectBattleCmd') {
        list=msg.attackableCards.map((c,i)=>({key:['attack',c.code,c.sequence,c.directAttack].join(':'),
            weight:28,response:msg.prepareResponse(B.ATTACK,P.IndexResponse(i))}));
        list.push(...msg.activatableCards.map((c,i)=>({key:'battle-effect:'+c.code+':'+c.desc,weight:7,
            response:msg.prepareResponse(B.ACTIVATE,P.IndexResponse(i))})));
        if (msg.canM2) list.push({key:'main2',weight:5,response:msg.prepareResponse(B.TO_M2)});
        if (msg.canEp) list.push({key:'end',weight:2,response:msg.prepareResponse(B.TO_EP)});
    } else list=engineOptions(msg,s,{...twinsPolicy,includeSequence:true});
    const visible=stateFeatures(duel,scene.bot);
    return list.map(c => {
        const key = scene.bot === 0 && ['SelectIdleCmd','SelectCard','SelectUnselectCard'].includes(name) ?
            c.key.replace(/(\d+):([01]):(\d+)/g, (_,id,seat,zone)=>id+':'+(1-Number(seat))+':'+zone) : c.key;
        return {...c,keys:twinsFeatures(s,visible,name,key,scene.bot)};
    });
}

function encodeFrontier(p) {
    const features=[];
    [p.board,p.hand,p.grave,[...new Set(p.extra)]].forEach((cards,slot)=>{
        for (const code of cards.filter(Boolean)) features.push('frontier/'+code+'/'+slot);
    });
    const scalars=[Math.min(5,p.summons),p.scoring_draws,p.set.filter(code=>[98360333,25156].includes(code)).length,
        Number(p.search_locked)];
    scalars.forEach((units,slot)=>{
        for (let n=0;n<units;n++) features.push('frontier/0/'+slot);
    });
    return features;
}

function commit(c, runtime, name) {
    const {s,policy,amazon,mag}=runtime;
    if (name==='SelectIdleCmd') {
        s.lastIdle=c.card;
        s.selectionHint=undefined;
        if (mag) policy.state.lastIdle=c.card;
    }
    if (amazon) policy.commit(name,c,'training');
    if (mag && c.attackSequence!==undefined) policy.attackSequence=c.attackSequence;
}

function terminal(duel, scene, runtime, winner) {
    const {s,policy,mag,amazon}=runtime, bot=scene.bot, own=snapshot(duel,bot);
    const removed=(s.startOpp ?? scene.field.filter(c=>c.player!==bot && [4,8].includes(c.location)).length) -
        snapshot(duel,1-bot).length;
    let retained, tie, twinBoard;
    if (mag) {
        const spells=own.filter(c=>c.location===8).map(c=>({...c,owner:c.owner===bot?0:1,
            equipCard:c.equipCard && {...c.equipCard,controller:c.equipCard.controller===bot?0:1}}));
        const result=settledScore(own.filter(c=>c.location===4),policy.state.lost,0,policy.state.draws,spells);
        tie=magistusTie(card,trap,policy,result);
        retained=result.score-5*s.drawsSent;
    } else if (amazon) {
        const view=policy.view(), result=boardScore(view,policy.state);
        tie=amazonessTie(C,view,policy.state,result);
        retained=Object.values(result).reduce((a,b)=>a+b,0)/10-5*s.drawsSent;
    } else {
        twinBoard=position(duel,s,bot);
        tie=twinsTie(twinBoard,{hardHit:s.interrupted>0});
    }
    return {winner,removed,retained,twinBoard,tie,damage:s.damage,main2:s.main2,drawsSent:s.drawsSent,
        denied:s.denied,opening:scene.turn===1};
}

function score(results, cfg) {
    const twins=results.filter(r=>r?.twinBoard);
    if (twins.length) {
        const r=spawnSync('mono',[cfg.scorer,cfg.cards],{encoding:'utf8',timeout:15000,
            input:twins.map(line=>JSON.stringify({position:line.twinBoard})).join('\n')+'\n'});
        if (r.status!==0) throw Error(r.stderr || 'Twins scorer failed');
        const items=r.stdout.trim().split(/\s+/).map(Number);
        if (items.length!==twins.length || items.some(n=>!Number.isFinite(n))) throw Error('Bad score count');
        twins.forEach((line,i)=>line.retained=items[i]-10*line.drawsSent);
    }
    for (const r of results.filter(r=>r && !r.error)) {
        r.pressure=10*r.removed+r.retained;
        r.outcome=r.opening?[r.winner,r.retained,r.denied,0,0,r.tie.score]:
            [r.winner,r.removed,r.damage,r.retained,r.tie.score,r.pressure];
    }
}

function pick(list, random, weights) {
    const ranked=list.map(c=>[c,Math.sqrt(Math.max(0.01,c.weight))*
        Math.exp(Math.max(-2,Math.min(2,c.keys.reduce((sum,key)=>sum+(weights[key]||0),0)/4)))]);
    let t=random()*ranked.reduce((sum,c)=>sum+c[1],0);
    return ranked.find(c=>(t-=c[1])<0)?.[0] ?? ranked.at(-1)[0];
}

async function play(engine, scene, model, args={}) {
    const runtime=prepare(engine,scene,model), {s,policy}=runtime;
    const duel=open(engine.core,scene);
    const steps=[],wire=[];
    let seat,maxMs=0,forced=false,lastFrontier;
    try {
        if (args.exe) seat=await openWindBot(args.exe,scene,args.onBotLog ?? quiet,scene.bot);
        for (let spin=0;spin<1400;spin++) for (const {message:raw} of duel.splitProcessResult(duel.process())) {
            if (!raw) continue;
            const name=nameOf(raw);
            if (name==='Retry') return {error:'illegal-response',wire};
            if (name==='Win' || name==='NewTurn' && s.turn===scene.turn) {
                if (args.force && !forced) return {error:'branch-unavailable'};
                if (policy) refresh(duel,scene.bot,m=>policy.observe(m),false);
                if (lastFrontier) lastFrontier.successor=encodeFrontier(position(duel,s,scene.bot));
                return {...terminal(duel,scene,runtime,name==='Win'?(raw.player===scene.bot?1:-1):0),steps,wire,maxMs};
            }
            if (!(raw instanceof P.YGOProMsgResponseBase)) {
                args.onEvent?.(name,raw);
                observe(duel,scene,runtime,raw);
                seat?.onMessage(raw);
                continue;
            }
            const player=raw.responsePlayer(), start=performance.now();
            let response;
            if (player!==scene.bot || s.turn<scene.turn || name==='SelectChain'&&!raw.chains.length) {
                response=opponent(raw,scene,s);
            } else if (seat) {
                refresh(duel,scene.bot,m=>seat.onMessage(m),true);
                response=await seat.choose(raw);
            } else {
                const msg=raw.playerView?.(scene.bot) ?? raw;
                if (lastFrontier && name==='SelectIdleCmd') {
                    lastFrontier.successor=encodeFrontier(position(duel,s,scene.bot));
                    lastFrontier=undefined;
                }
                if (policy) refresh(duel,scene.bot,m=>policy.observe(m),false);
                let list=[];
                try { list=choices(duel,scene,runtime,msg,model); }
                catch (e) { if (!(e instanceof UnsupportedPrompt)) throw e; }
                let chosen,wanted;
                if (args.replay && wire.length<args.replay.length) wanted=args.replay[wire.length];
                else if (args.force && wire.length<=args.force.offset) {
                    const pivot=wire.length===args.force.offset;
                    wanted=pivot?args.force.action:args.force.prefix[wire.length];
                    if (pivot) forced=list.some(c=>encoded(c.response)===wanted);
                    if (pivot&&!forced) return {error:'branch-unavailable'};
                }
                if (wanted) { response=Buffer.from(wanted,'base64');chosen=list.find(c=>encoded(c.response)===wanted); }
                else if (args.random && list.length) chosen=pick(list,args.random,model.weights);
                else if (policy) response=policy.choose(msg);
                if (!chosen && response) chosen=list.find(c=>encoded(c.response)===encoded(response));
                if (!chosen && !response && list.length) chosen=list.reduce((a,b)=>b.weight>a.weight?b:a);
                if (chosen && (args.random || wanted)) commit(chosen,runtime,name);
                response ??= chosen?.response ?? opponent(msg,scene,s);
                if (chosen && list.length>1 && ['SelectIdleCmd','SelectChain','SelectBattleCmd','SelectCard'].includes(name)) {
                    const step={offset:wire.length,name,action:encoded(response),keys:chosen.keys,
                        alternatives:list.filter(c=>encoded(c.response)!==encoded(response)).slice(0,12).map(c=>encoded(c.response))};
                    steps.push(step);
                    if (!policy && name==='SelectIdleCmd' && [1,4,256].includes(s.phase)) lastFrontier=step;
                }
            }
            if (!response) return {error:'empty-response:'+name,wire};
            args.onResponse?.(wire.length,raw,response);
            maxMs=Math.max(maxMs,performance.now()-start);
            wire.push(encoded(response));
            duel.setResponse(response);
        }
        return {error:'turn-budget',wire};
    } catch(e) { return {error:e.message,wire}; }
    finally { duel.endDuel();seat?.close(); }
}

const compare=(a,b)=>compareTurn(a.outcome,b.outcome,a.opening);
module.exports={play,score,compare,encodeFrontier};
