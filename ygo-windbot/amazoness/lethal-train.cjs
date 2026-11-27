'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { rollout, verified, deckFrom, makeCases, search } = require('./full-train.cjs');
const { AmazonessPolicy } = require('./live-policy.cjs');
const { C, F } = require('./duel-policy.cjs');

const read = file => JSON.parse(fs.readFileSync(file,'utf8'));
const hashFile = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const hasWin = result => result?.outcome?.[0] === 1;
const preserveLethal = (msg,name,choices,view,state) =>
    AmazonessPolicy.prototype.preserveLethal.call({ seat:state.bot, state },msg,name,choices,view);

function chooseCases(deck, incumbentInputs) {
    const extra = [
        { code:15025844, position:1 }, { code:89631139,position:4 }, { code:46986414,position:1 },
        { code:28985331,position:1 }, { code:7572887,position:4 }, { code:14558128,position:1 },
    ];
    const key = input => input.main.slice(0,6).sort((a,b)=>a-b).join(',');
    const used = new Set([...incumbentInputs.train, ...incumbentInputs.holdout].filter(i=>i.bot===1).map(key));
    const raw = makeCases(deck,180,0,11990601).train.filter(i=>i.bot===1 && !used.has(key(i)));
    return raw.slice(0,96).map((input,index) => {
        if (index % 3 === 0) return input;
        const one = extra[index % extra.length];
        const two = extra[(index+3)%extra.length];
        const cards = index % 4 === 1 ? [one] : [one,two];
        const backrow = input.setup.filter(c=>c.location===8);
        const setup = [...cards.map((card,sequence)=>({...card,sequence,location:4})), ...backrow];
        const enemyLP = [1800,2500,3300,4100,4800,5900,6800,8000][index%8];
        return {...input,setup,enemyLP};
    });
}

function fit(pairs) {
    const delta = {};
    for (let epoch=0;epoch<8;epoch++) {
        for (const pair of pairs) {
            if (pair.axis !== 0 || !pair.high.some(k=>/^a:Select(IdleCmd|BattleCmd|Card):/.test(k))) continue;
            const useful = key => /^phase:(4|8|16|32|64|128):/.test(key);
            const positive = pair.high.filter(k=>useful(k)&&!pair.low.includes(k));
            const negative = pair.low.filter(k=>useful(k)&&!pair.high.includes(k));
            const absolute = keys => keys.reduce((sum,key)=>sum+(delta[key]||0),0);
            const step = 0.58/(1+Math.exp(absolute(positive)-absolute(negative)));
            for (const [keys,direction] of [[positive,1],[negative,-1]]) {
                for (const key of keys) delta[key]=Math.max(-3,Math.min(3,(delta[key]||0)+step*direction));
            }
        }
    }
    return delta;
}

function adjust(baseline, delta, scale) {
    const weights = {...baseline};
    for (const [key, value] of Object.entries(delta)) weights[key]=(weights[key]||0)+scale*value;
    return weights;
}

function play(engine,input,model) {
    return rollout(engine,input,{model,actionFilter:preserveLethal,deadline:Date.now()+3000});
}

function summary(results) {
    const good=results.filter(r=>r?.outcome);
    const nonLethal=good.filter(r=>!hasWin(r));
    return {
        settled:good.length, lethal:good.filter(hasWin).length,
        nonLethalMain2:nonLethal.filter(r=>r.main2).length,
        noLethalNoMain2:nonLethal.filter(r=>!r.main2).length,
        cleared:good.reduce((sum,r)=>sum+r.outcome[1],0),
        damage:good.reduce((sum,r)=>sum+r.outcome[2],0),
    };
}

async function main() {
    const [fixture,deckFile,incumbentFile,incumbentInputFile,output,...data]=process.argv.slice(2);
    if (!output || !data.length) throw new Error('Supply FIXTURE DECK MODEL INCUMBENT_INPUTS OUTPUT DATA [OVERLAY]');
    const model=read(incumbentFile);
    const policySha256=hashFile(require.resolve('./duel-policy.cjs'));
    if (model.policySha256!==policySha256 || model.deckSha256!==hashFile(deckFile)) throw new Error('Incompatible base model');
    const engine=await loadEngine(path.resolve(fixture),data.map(dir=>path.resolve(dir)));
    const inputs=chooseCases(deckFrom(deckFile),read(incumbentInputFile));
    const train=inputs.slice(0,54), tuning=inputs.slice(54,72), holdout=inputs.slice(72);
    fs.mkdirSync(output,{recursive:true});
    const write=(file,content)=>fs.writeFileSync(path.join(output,file),JSON.stringify(content,null,2)+'\n');
    write('inputs.json',{train,tuning,holdout,deckSha256:model.deckSha256,policySha256,incumbentSha256:hashFile(incumbentFile)});
    const learned=[];
    for (const input of train) {
        const result=search(engine,input,{attempts:120,caseMs:1100,actionFilter:preserveLethal});
        learned.push(result);
        write('train-'+input.id+'.json',result);
    }
    const pairs=learned.flatMap(row=>row.pairs).filter(row=>row.axis===0);
    if (!pairs.length) throw new Error('No replay-verified lethal comparisons');
    const delta=fit(pairs);
    const tuningRows=[0,0.45,0.9,1.5,2.2].map(scale=>{
        const weights=adjust(model.weights[1],delta,scale);
        return {scale,result:summary(tuning.map(input=>play(engine,input,weights))),weights};
    });
    const baselineTune=tuningRows[0].result;
    const selected=tuningRows.filter(r=>r.result.settled===tuning.length &&
        r.result.noLethalNoMain2<=baselineTune.noLethalNoMain2).sort((a,b)=>
        b.result.lethal-a.result.lethal || b.result.cleared-a.result.cleared ||
        b.result.damage-a.result.damage || a.scale-b.scale)[0] || tuningRows[0];
    const candidate={...model,weights:{...model.weights,1:selected.weights},
        refinement:{incumbentSha256:hashFile(incumbentFile),scale:selected.scale,lethalPairs:pairs.length,
            trainerSha256:hashFile(__filename),replayTrainerSha256:hashFile(require.resolve('./full-train.cjs'))}};
    write('candidate.json',candidate);
    const baseline=[], refined=[], reference=[], replays=[];
    for (const input of holdout) {
        const old=play(engine,input,model.weights[1]), improved=play(engine,input,selected.weights);
        const best=search(engine,input,{attempts:115,caseMs:1100,actionFilter:preserveLethal}).best;
        baseline.push(old);refined.push(improved);reference.push(best);
        const row={id:input.id,baseline:verified(engine,input,old,preserveLethal),
            candidate:verified(engine,input,improved,preserveLethal),reference:verified(engine,input,best,preserveLethal)};
        replays.push(row);
        write('holdout-'+input.id+'.json',{old,improved,best,replayed:row});
    }
    const report={training:{hands:train.length,lethalCases:learned.filter(r=>hasWin(r.best)).length,
        lethalPairs:pairs.length,adjustedWeights:Object.keys(delta).length},
        tuning:tuningRows.map(({scale,result})=>({scale,...result})),selectedScale:selected.scale,
        holdout:{hands:holdout.length,baseline:summary(baseline),candidate:summary(refined),reference:summary(reference),
            replays},errors:[...engine.engineErrors]};
    write('summary.json',report);
    console.log(JSON.stringify(report));
    if (engine.engineErrors.size || replays.some(row=>!row.baseline||!row.candidate||!row.reference)) process.exitCode=1;
}

if (require.main===module) main().catch(error=>{console.error(error.stack);process.exitCode=1;});
