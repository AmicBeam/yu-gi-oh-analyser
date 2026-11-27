'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { modelHash } = require('../Models/pack-turn-models.cjs');

const load = (base, file) => JSON.parse(fs.readFileSync(path.join(base,file),'utf8'));
const save = (base, file, value) => fs.writeFileSync(path.join(base,file),JSON.stringify(value)+'\n');

function merge(output, dirs) {
    assert(dirs.length>=2);
    const batches=dirs.map(dir=>({dir,manifest:load(dir,'manifest.json'),report:load(dir,'summary.json'),
        scenes:load(dir,'scenarios.json').filter(scene=>scene.split==='holdout')}));
    const {manifest,report:first}=batches[0], deck=manifest.deck, file=deck+'.turn-ranker.json';
    const candidate=load(batches[0].dir,path.join('candidate','Models',file));
    const keys=new Set(), seeds=new Set(), games=[], ranking={current:0,candidate:0,total:0};
    const holdout={pairs:0,hands:0,generated:0,verified:0,categories:[]};
    for (const [i,batch] of batches.entries()) {
        assert.equal(batch.manifest.deck,deck);
        assert.equal(batch.manifest.model,manifest.model);
        assert.equal(modelHash(load(batch.dir,path.join('candidate','Models',file))),modelHash(candidate));
        assert.deepEqual(batch.report.counts.train,first.counts.train);
        for (const scene of batch.scenes) {
            assert(!keys.has(scene.key)&&!seeds.has(scene.seed),'Overlapping holdout');
            keys.add(scene.key);
            seeds.add(scene.seed);
        }
        for (const key of ['current','candidate','total']) ranking[key]+=batch.report.ranking[key];
        for (const key of ['pairs','hands','generated','verified']) holdout[key]+=batch.report.counts.holdout[key];
        holdout.categories.push(...batch.report.counts.holdout.categories);
        games.push(...batch.report.games.map(game=>({...game,id:'batch-'+i+'/'+game.id})));
    }
    holdout.categories=[...new Set(holdout.categories)];
    const wins=games.filter(game=>game.sign===1).length,losses=games.filter(game=>game.sign===-1).length;
    const lostLethal=games.filter(game=>game.current.winner===1 && game.candidate.winner!==1).map(game=>game.id);
    const failed=games.filter(game=>game.sign===null || game.candidate.maxMs>5000).map(game=>game.id);
    const engineErrors=batches.flatMap(batch=>batch.report.engineErrors), counts={train:first.counts.train,holdout};
    const enough=Object.entries(first.quota).every(([split,limit])=>counts[split].pairs>=limit.pairs &&
       counts[split].hands>=limit.hands && counts[split].categories.length>=3);
    const promoted=enough && ranking.candidate>ranking.current && wins>losses && !lostLethal.length &&
        !failed.length && new Set(games.map(game=>game.kind)).size===6 && !engineErrors.length;
    const result={deck,scale:first.scale,quota:first.quota,enough,counts,ranking,wins,losses,
        equal:games.filter(game=>game.sign===0).length,lostLethal,failed,engineErrors,promoted,games};
    fs.mkdirSync(path.join(output,'candidate','Models'),{recursive:true});
    save(output,'manifest.json',{...manifest,mergedAudits:dirs.map(dir=>path.resolve(dir))});
    save(output,'scenarios.json',batches.flatMap(batch=>batch.scenes));
    save(output,'summary.json',result);
    save(path.join(output,'candidate','Models'),file,candidate);
    return result;
}

if (require.main===module) {
    const [output,...dirs]=process.argv.slice(2);
    const result=merge(output,dirs);
    console.log(JSON.stringify({...result,games:result.games.length}));
}
module.exports={merge};
