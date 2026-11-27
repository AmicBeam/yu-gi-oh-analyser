'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { baseModels, modelHash, calibrationPath, calibrated } = require('../Models/pack-turn-models.cjs');

const load = file => JSON.parse(fs.readFileSync(file,'utf8'));
function replace(name,data) {
    const temp=name+'.'+process.pid+'.tmp';
    fs.writeFileSync(temp,JSON.stringify(data)+'\n');
    fs.renameSync(temp,name);
}

function promote(run, models) {
    const summary=load(path.join(run,'summary.json')), manifest=load(path.join(run,'manifest.json'));
    const deck=manifest.deck, base=baseModels()[deck];
    assert(base && summary.deck===deck);
    assert(summary.promoted && summary.enough && summary.games.length>=18 &&
        summary.ranking.candidate>summary.ranking.current && summary.wins>summary.losses &&
        !summary.lostLethal.length && !summary.failed.length && !summary.engineErrors.length,
    'This run did not pass the local promotion gate');
    assert(!summary.games.some(game=>game.current.winner===1 && game.candidate.winner!==1),
        'At least one established lethal became nonlethal');
    const file=deck+'.turn-ranker.json', active=path.join(models,file), pinned=calibrationPath(deck);
    const current=load(active), candidate=load(path.join(run,'candidate','Models',file));
    assert.equal(modelHash(current),manifest.model,'Active model changed after the A/B');
    assert.equal(modelHash(calibrated(deck,base)),manifest.model,'Packaged model differs from the measured current model');
    const diff=(before,after)=>Object.fromEntries(Object.entries(after).filter(([key,value])=>
        JSON.stringify(value)!==JSON.stringify(before[key])));
    const delta = {base:modelHash(base),weights:diff(base.weights,candidate.weights),
        ...(base.frontier && {frontier:diff(base.frontier,candidate.frontier)}),
        ...(candidate.tieFrontier && base.frontier && {tieFrontier:diff(base.tieFrontier ?? base.frontier,candidate.tieFrontier)})};
    const installed={...base,weights:{...base.weights,...delta.weights},
        ...(base.frontier&&{frontier:{...base.frontier,...delta.frontier}}),
        ...(delta.tieFrontier&&{tieFrontier:{...(base.tieFrontier ?? base.frontier),...delta.tieFrontier}})};
    assert.deepEqual(installed,candidate,'Candidate has changes outside the supported weight fields');
    const previous=fs.existsSync(pinned)?load(pinned):undefined;
    replace(path.join(run,'previous-model.json'),current);
    try {
        replace(pinned,delta);
        replace(active,installed);
        assert.deepEqual(load(active),candidate);
        assert.deepEqual(calibrated(deck,base),candidate);
    } catch(error) {
        if (previous) replace(pinned,previous);
        else if (fs.existsSync(pinned)) fs.unlinkSync(pinned);
        replace(active,current);
        throw error;
    }
    replace(path.join(run,'promotion.json'),{deck,previous:modelHash(current),installed:modelHash(installed),active});
}

if (require.main===module) {
    assert(process.argv.length===4,'Pass the gated run directory and local Models directory');
    promote(path.resolve(process.argv[2]),path.resolve(process.argv[3]));
}
module.exports={promote};
