'use strict';

const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { verified, compare } = require('./full-train.cjs');

function sha(bytes) { return createHash('sha256').update(bytes).digest('hex'); }

async function main() {
    const [directory, fixture, ...data] = process.argv.slice(2);
    if (!directory || !fixture || !data.length) throw new Error('Require RUN FIXTURE DATA [OVERLAY]');
    const read = name => JSON.parse(fs.readFileSync(path.join(directory,name),'utf8'));
    const inputs = read('inputs.json'), stats = read('summary.json'), model = read('candidate.json');
    assert.equal(inputs.trainerSha256,sha(fs.readFileSync(require.resolve('./full-train.cjs'))));
    assert.equal(inputs.policySha256,sha(fs.readFileSync(require.resolve('./duel-policy.cjs'))));
    assert.equal(model.deckSha256,inputs.deckSha256);
    assert.equal(model.policySha256,inputs.policySha256);
    assert.equal(model.trainerSha256,inputs.trainerSha256);
    assert([0,1].every(bot=>Object.values(model.weights[bot]).every(Number.isFinite)));
    const keys = [...inputs.train,...inputs.holdout].map(input => input.main.slice(0,input.bot?6:5)
        .sort((a,b)=>a-b).join(','));
    assert.equal(keys.length,new Set(keys).size);
    const engine=await loadEngine(path.resolve(fixture),data.map(p=>path.resolve(p)));
    for (const [code,digest] of Object.entries(inputs.scripts||{})) {
        const props=engine.cardReader(Number(code));
        const lua=[props.ruleCode,props.alias,code].filter(Boolean)
            .map(id=>engine.scriptReader('script/c'+id+'.lua')).find(Boolean);
        assert(lua,'Missing script '+code);
        assert.equal(sha(lua),digest,'Script digest '+code);
    }
    const errors=[], replays={training:0,rule:0,candidate:0,reference:0};
    const baselineRegressions=[], missedLethal=[];
    for (const input of inputs.train) {
        const trial=read('case-'+input.id+'.json').best;
        if (!trial?.outcome || !verified(engine,input,trial)) errors.push([input.id,'training']);
        else replays.training++;
    }
    const evidence={};
    for (const input of [...inputs.holdout,...inputs.directed]) {
        const traces=read('holdout-'+input.id+'.json');
        evidence[input.id]=traces;
        for (const role of ['rule','candidate','reference']) {
            const result=traces[role];
            if (!result?.outcome || !verified(engine,input,result)) errors.push([input.id,role,result?.error]);
            else replays[role]++;
        }
        const {rule,candidate,reference}=traces;
        if (!candidate?.outcome || rule?.outcome && compare(candidate.outcome,rule.outcome)<0)
            baselineRegressions.push(input.id);
        if (input.bot===1 && reference?.outcome?.[0]===1 && candidate?.outcome?.[0]!==1) missedLethal.push(input.id);
    }
    const counts={};
    for (const role of ['rule','candidate','reference']) {
        const second=stats.holdout.filter(row=>row.bot===1 && row[role]);
        counts[role]={
            settled:stats.holdout.filter(row=>row[role]).length,
            lethal:second.filter(row=>row[role][0]===1).length,
            nonLethalMain2:second.filter(row=>row[role][0]===0 && evidence[row.id][role].main2).length,
        };
    }
    const report={replays,errors,engineErrors:[...engine.engineErrors],counts,baselineRegressions,missedLethal,
        directed:stats.directed,accepted:!errors.length && !engine.engineErrors.size &&
            !baselineRegressions.length && !missedLethal.length};
    console.log(JSON.stringify(report,null,2));
}

main().catch(error=>{console.error(error.stack);process.exitCode=1;});
