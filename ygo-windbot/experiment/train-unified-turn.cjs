'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseArgs } = require('node:util');
const { createHash } = require('node:crypto');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { play, score, compare, encodeFrontier } = require('./turn-matchup.cjs');

const DECKS = { Twins2:'../Decks/AI_Twins2.ydk', Magistus:'../magistus/AI_Magistus.ydk', Amazoness:'../Decks/AI_Amazoness.ydk' };
const QUOTA = { train: {pairs:36,hands:12}, holdout:{pairs:18,hands:8} };
const TYPES = ['opening','opening-pressure','breaking-lethal','breaking-pressure','followup','followup-pressure'];
const TRAPS = [14558128,10045474,27204311,42141493,97268402,73642296,94145022];
const hash = value => createHash('sha256').update(value).digest('hex');
const load = name => JSON.parse(fs.readFileSync(name,'utf8'));
const save = (name,value) => fs.writeFileSync(name,JSON.stringify(value)+'\n');
const rng = n => () => ((n=(Math.imul(n,1664525)+1013904223)>>>0)/2**32);
function shuffle(values,random) {
    const result=[...values];
    for (let i=result.length-1;i>0;i--) { const j=Math.floor(random()*(i+1));[result[i],result[j]]=[result[j],result[i]]; }
    return result;
}
function ydk(name) {
    const file=path.join(__dirname,DECKS[name]), content=fs.readFileSync(file,'utf8');
    const [,main,extra]=content.split(/#main|#extra|!side/);
    return {main:main.trim().split(/\s+/).map(Number),extra:extra.trim().split(/\s+/).map(Number)};
}

function scenes(name,count,seed,exclude = [], openingFocus = false) {
    const deck=ydk(name), random=rng(seed), seen=new Set(exclude), output=[];
    const follows={Twins2:[25150,73810864],Magistus:[42544773],Amazoness:[101307006,101307074]};
    // Same scene families, seat, pressure windows and LP bands for every deck.
    // Split by family variant and by the exact cards available on the tested turn.
    for (const [split,offset] of [['train',0],['holdout',1]]) for (let n=0;n<count;n++) {
        const focused=openingFocus && split==='train' && n%3!==0;
        const kind=focused ? (n%2===0?'opening':'opening-pressure') : TYPES[n%TYPES.length];
        const variant=Math.floor(n/TYPES.length)*2+offset;
        const turn=kind.startsWith('opening')?1:kind.startsWith('breaking')?2:3, bot=turn===2?1:0;
        const field=[], pool=[...deck.main], pressure=kind.endsWith('pressure');
        const enemy=1-bot;
        const place=(code,location,sequence,position,player=enemy)=>field.push({code,location,sequence,position,player});
        if (turn!==1) {
            const boards=[[],[[46986414,1]],[[89631139,4]],[[7572887,1],[46986414,4]],[[31305911,4]],[[46986414,1],[89631139,4]]];
            const layouts=pressure?[[[46986414,4]],...boards.slice(1)]:boards;
            const chosen=layouts[variant%layouts.length];
            chosen.forEach(([code,position],i)=>place(code,4,i,position));
            if (pressure && variant%3!==0) place(variant%3===1?44095762:41420027,8,2,8);
        }
        if (turn===3) for (const [i,code] of follows[name].entries()) {
            const index=pool.indexOf(code);
            if (index>=0) { pool.splice(index,1); place(code,16,i,1,bot); }
        }
        let main,key;
        do {
            const head=focused?shuffle(pressure?[36326160,73810864,27204311]:[25150,25156,65681983],random):[];
            const rest=[...pool];
            for (const code of head) rest.splice(rest.indexOf(code),1);
            const permuted=shuffle(rest,random);
            main=focused?[...shuffle([...head,...permuted.slice(0,5-head.length)],random),...permuted.slice(5-head.length)]:permuted;
            key=main.slice(0,5).sort((a,b)=>a-b).join(',');
        } while (seen.has(key));
        seen.add(key);
        const lp=[8000,8000];
        if (kind==='breaking-lethal') lp[enemy]=[1300,2500,3600,5200][variant%4];
        if (turn===3) lp[enemy]=[8000,5200,6800,3700][variant%4];
        const traps=pressure?[focused && variant%4!==0?10045474:TRAPS[variant%TRAPS.length],...(variant%4===3?[TRAPS[(variant+2)%TRAPS.length]]:[])]:[];
        const foe={main:[...traps,...Array(40-traps.length).fill(74677422)],extra:[]};
        output.push({id:split+'-'+n,split,kind,variant,turn,bot,deck:name,key,field,lp,
            seed:Math.floor(random()*2**32),decks:bot?[foe,{main,extra:deck.extra}]:[{main,extra:deck.extra},foe]});
    }
    return output;
}

const primary = r=>r.opening?r.outcome.slice(0,5):[r.outcome[0],r.outcome[5],r.outcome[2]];
const margin=(weights,delta)=>Object.entries(delta).reduce((total,[key,v])=>total+(weights[key]||0)*v,0);

function delta(a,b) {
    const d=Object.create(null);
    for (const key of a) d[key]=(d[key]||0)+1;
    for (const key of b) d[key]=(d[key]||0)-1;
    return Object.fromEntries(Object.entries(d).filter(([,v])=>v));
}

async function collect(engine, scene, model, cfg) {
    const random=rng(scene.seed^903621), runs=[], errors={};
    for (let k=0;k<cfg.attempts;k++) {
        let force;
        if (k%3 && runs.length) {
            const pivots=runs[k%runs.length];
            const relevant=pivots.steps.filter(s=>s.offset>=0 && s.alternatives.length);
            if (relevant.length) {
                const p=relevant[Math.floor(random()*relevant.length)];
                force={offset:p.offset,action:p.alternatives[Math.floor(random()*p.alternatives.length)],prefix:pivots.wire};
            }
        }
        const r=await play(engine,scene,model,{random,force});
        if (r.error) { errors[r.error]=(errors[r.error]||0)+1;continue; }
        if (!runs.some(a=>a.wire.join('|')===r.wire.join('|'))) runs.push(r);
    }
    score(runs,cfg);
    const groups=new Map();
    for (const run of runs) {
        if (run.winner!==0) continue;
        const main=JSON.stringify(primary(run));
        for (const step of run.steps) {
            const prefix=hash(run.wire.slice(0,step.offset).join('|'));
            const key=prefix+'|'+main;
            if (!groups.has(key)) groups.set(key,new Map());
            const actions=groups.get(key), old=actions.get(step.action);
            if (!old || run.tie.score>old.run.tie.score) actions.set(step.action,{run,step,prefix});
        }
    }
    const pairs=[], replayed=new Map();
    async function verify(run) {
        if (!replayed.has(run)) {
            const other=await play(engine,scene,model,{replay:run.wire});
            if (!other.error) score([other],cfg);
            replayed.set(run,!other.error && JSON.stringify(run.outcome)===JSON.stringify(other.outcome) &&
                JSON.stringify(run.tie.details)===JSON.stringify(other.tie.details) &&
                run.wire.join('|')===other.wire.join('|'));
        }
        return replayed.get(run);
    }
    const first=shuffle([...groups.values()],random).sort((a,b)=>b.size-a.size);
    const used=new Set();
    for (const group of first) {
        const ranked=[...group.values()].sort((a,b)=>b.run.tie.score-a.run.tie.score);
        if (ranked.length<2) continue;
        const high=ranked[0],low=ranked.at(-1);
        if (high.run.tie.score===low.run.tie.score || used.has(high.prefix)) continue;
        const keys=(step)=>step.successor?[...step.keys,...step.successor]:step.keys;
        const features=cfg['terminal-ties']?
            delta(encodeFrontier(high.run.twinBoard),encodeFrontier(low.run.twinBoard)) :
            delta(keys(high.step),keys(low.step));
        if (!Object.keys(features).length || !await verify(high.run) || !await verify(low.run)) continue;
        used.add(high.prefix);
        pairs.push({prefix:high.prefix,prompt:high.step.name,offset:high.step.offset,
            featureDelta:features,primary:primary(high.run),high:high.run.tie,low:low.run.tie});
        if (pairs.length>=4) break;
    }
    return {id:scene.id,kind:scene.kind,split:scene.split,key:scene.key,settled:runs.length,
        errors,pairs,verified: [...replayed.values()].filter(Boolean).length};
}

function flat(model, terminalOnly = false) {
    const weights=terminalOnly?{}:{...model.weights};
    const frontier=terminalOnly?(model.tieFrontier ?? model.frontier):model.frontier;
    for (const [code,slots] of Object.entries(frontier??{}))
        slots.forEach((v,slot)=>{ weights['frontier/'+code+'/'+slot]=v; });
    return weights;
}

function fit(model,train,scale=1,terminalOnly=false) {
    const initial=flat(model,terminalOnly), weights={...initial}, random=rng(675021);
    for (let epoch=0;epoch<10;epoch++) for (const pair of shuffle(train,random)) {
        const d=pair.featureDelta;
        const step=.10/(1+Math.exp(Math.max(-12,Math.min(12,margin(weights,d)))))/Math.sqrt(Object.keys(d).length);
        for (const [key,v] of Object.entries(d)) weights[key]=Math.max(-4,Math.min(4,(weights[key]||0)+step*v));
    }
    for (const key of Object.keys(weights))
        weights[key]=(initial[key]??0)+scale*(weights[key]-(initial[key]??0));
    const frontier={};
    for (const key of Object.keys(weights).filter(k=>k.startsWith('frontier/'))) {
        const [,code,slot]=key.split('/');
        (frontier[code]??=[0,0,0,0])[Number(slot)]=weights[key];
        delete weights[key];
    }
    return terminalOnly ? {...model,tieFrontier:frontier} : {...model,weights,...(model.frontier && {frontier})};
}

function countRows(rows) {
    return {pairs:rows.reduce((n,r)=>n+r.pairs.length,0),hands:rows.filter(r=>r.pairs.length).length,
        generated:rows.length,verified:rows.reduce((n,r)=>n+r.verified,0),categories:[...new Set(rows.filter(r=>r.pairs.length).map(r=>r.kind))]};
}
function loadRows(folder) {
    return fs.readdirSync(folder).filter(n=>n.endsWith('.json')).sort().map(n=>load(path.join(folder,n)));
}

function runtime(cfg, label, model) {
    const dest=path.join(cfg.output,label);
    fs.mkdirSync(path.join(dest,'Models'),{recursive:true});
    save(path.join(dest,'Models',cfg.deck+'.turn-ranker.json'),model);
    if (cfg.deck!=='Twins2') return;
    const exe=path.join(dest,'WindBot.exe'), source=path.dirname(cfg.windbot);
    for (const item of fs.readdirSync(source)) if (/\.(exe\.config|dll|cdb)$/.test(item)||['Decks','Dialogs','WindBot.exe'].includes(item)) {
        const target=path.join(dest,item);
        if (fs.existsSync(target)) continue;
        if (item==='WindBot.exe') fs.copyFileSync(path.join(source,item),target);
        else fs.symlinkSync(path.join(source,item),target);
    }
    return exe;
}

async function audit(engine, pool, model, candidate, cfg) {
    const currentExe=runtime(cfg,'current',model), candidateExe=runtime(cfg,'candidate',candidate);
    const rows=[];
    for (const scene of pool) {
        const a=await play(engine,scene,model,{exe:currentExe});
        const b=await play(engine,scene,candidate,{exe:candidateExe});
        score([a,b],cfg);
        const sign=a.error||b.error?null:Math.sign(compare(b,a));
        const first=a.wire?.findIndex((v,i)=>b.wire?.[i]!==v);
        const brief=({steps,wire,twinBoard,...r})=>r;
        rows.push({id:scene.id,kind:scene.kind,sign,firstDifference:first,current:brief(a),candidate:brief(b)});
    }
    return rows;
}

async function main() {
    const {values}=parseArgs({options:{fixture:{type:'string'},data:{type:'string',multiple:true},models:{type:'string'},
        deck:{type:'string'},output:{type:'string'},windbot:{type:'string'},scorer:{type:'string'},cards:{type:'string'},
        count:{type:'string',default:'72'},attempts:{type:'string',default:'64'},audit:{type:'string',default:'24'},
        seed:{type:'string',default:'2026112715'},mode:{type:'string',default:'both'},
        scale:{type:'string',default:'1'},'training-source':{type:'string'},
        'exclude-scenarios':{type:'string',multiple:true},'opening-focus':{type:'boolean'},'terminal-ties':{type:'boolean'}}});
    const cfg={...values,count:Number(values.count),attempts:Number(values.attempts),audit:Number(values.audit),seed:Number(values.seed),scale:Number(values.scale)};
    assert(DECKS[cfg.deck] && cfg.data?.length && cfg.output && cfg.models && cfg.fixture);
    if (cfg.deck==='Twins2') assert(cfg.windbot&&cfg.scorer&&cfg.cards);
    if (cfg['opening-focus'] || cfg['terminal-ties']) assert.equal(cfg.deck,'Twins2');
    fs.mkdirSync(cfg.output,{recursive:true});const shards=path.join(cfg.output,'shards');fs.mkdirSync(shards,{recursive:true});
    const current=load(path.join(cfg.models,cfg.deck+'.turn-ranker.json'));
    const source=cfg['training-source'];
    assert(Number.isFinite(cfg.scale) && cfg.scale>0 && cfg.scale<=1);
    const previous=[...(source ? load(path.join(source,'scenarios.json')) : []),
        ...(cfg['exclude-scenarios']??[]).flatMap(file=>load(file))];
    const reused=source ? loadRows(path.join(source,'shards')).filter(row=>row.split==='train') : [];
    if (source) {
        const origin=load(path.join(source,'manifest.json'));
        assert.equal(origin.deck,cfg.deck);
        assert.equal(Boolean(origin.terminalTies),Boolean(cfg['terminal-ties']),
            'Training source uses a different ranking feature set');
        assert.equal(origin.model,hash(JSON.stringify(current)));
        assert.deepEqual(origin.data,cfg.data);
    }
    const pool=scenes(cfg.deck,cfg.count,cfg.seed,previous.map(s=>s.key),cfg['opening-focus'])
        .filter(scene=>!source || scene.split==='holdout');
    const signature={deck:cfg.deck,seed:cfg.seed,count:cfg.count,attempts:cfg.attempts,data:cfg.data,model:hash(JSON.stringify(current)),
        scale:cfg.scale,terminalTies:Boolean(cfg['terminal-ties']),...(source&&{trainingSource:path.resolve(source),trainingRows:hash(JSON.stringify(reused))}),
        scenes:hash(JSON.stringify(pool)),runner:hash(fs.readFileSync(__filename)),engineAdapter:hash(fs.readFileSync(require.resolve('./turn-matchup.cjs')))};
    const manifest=path.join(cfg.output,'manifest.json');
    if (fs.existsSync(manifest)) assert.deepEqual(load(manifest),signature);
    else save(manifest,signature);
    save(path.join(cfg.output,'scenarios.json'),pool);
    const engine=await loadEngine(cfg.fixture,cfg.data);
    try {
        if (cfg.mode!=='audit') {
            // Interleave splits so short runs produce independent validation material as well.
            const ordered=pool.sort((a,b)=>Number(a.id.split('-').at(-1))-Number(b.id.split('-').at(-1)));
            for (const scene of ordered) {
                const file=path.join(shards,scene.id+'.json');
                if (fs.existsSync(file)) continue;
                const row=await collect(engine,scene,current,cfg);
                if (engine.engineErrors.size) { row.engineErrors=[...engine.engineErrors];row.pairs=[];engine.engineErrors.clear(); }
                save(file,row);
                console.log(JSON.stringify({deck:cfg.deck,id:row.id,kind:row.kind,settled:row.settled,ties:row.pairs.length}));
            }
        }
        if (cfg.mode==='collect') return;
        const rows=loadRows(shards), train=[...reused,...rows.filter(r=>r.split==='train')], hold=rows.filter(r=>r.split==='holdout');
        const counts={train:countRows(train),holdout:countRows(hold)};
        const model=fit(current,train.flatMap(r=>r.pairs),cfg.scale,cfg['terminal-ties']);
        const validation=hold.flatMap(r=>r.pairs), hit=weights=>validation.filter(p=>margin(weights,p.featureDelta)>0).length;
        const ranking={current:hit(flat(current,cfg['terminal-ties'])),candidate:hit(flat(model,cfg['terminal-ties'])),total:validation.length};
        const enough=Object.entries(QUOTA).every(([split,threshold])=>counts[split].pairs>=threshold.pairs &&
            counts[split].hands>=threshold.hands && counts[split].categories.length>=3);
        // Use held-out cases in all six families, irrespective of whether a tie was found.
        const gatePool=pool.filter(s=>s.split==='holdout').slice(0,cfg.audit);
        const games=await audit(engine,gatePool,current,model,cfg);
        const wins=games.filter(g=>g.sign===1).length, losses=games.filter(g=>g.sign===-1).length;
        const lostLethal=games.filter(g=>g.current.winner===1&&g.candidate.winner!==1).map(g=>g.id);
        const failed=games.filter(g=>g.sign===null||g.candidate.maxMs>5000).map(g=>g.id);
        const promoted=enough && ranking.candidate>ranking.current && wins>losses &&
            !lostLethal.length && !failed.length && new Set(games.map(g=>g.kind)).size===TYPES.length &&
            !engine.engineErrors.size;
        const result={deck:cfg.deck,scale:cfg.scale,quota:QUOTA,enough,counts,ranking,wins,losses,
            equal:games.filter(g=>g.sign===0).length,lostLethal,failed,engineErrors:[...engine.engineErrors],promoted,games};
        save(path.join(cfg.output,'summary.json'),result);
        console.log(JSON.stringify({...result,games:games.length}));
    } finally { engine.core.finalize(); }
}

if (require.main===module) main().catch(e=>{console.error(e.stack);process.exitCode=1;});
module.exports={scenes,fit};
