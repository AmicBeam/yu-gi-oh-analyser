'use strict';

const fs = require('node:fs');
const { createHash } = require('node:crypto');
const path = require('node:path');

function mergeAmazoness(model) {
    if (model.version === 2) return model;
    const maps = Object.values(model.weights);
    const keys = new Set(maps.flatMap(m => Object.keys(m)));
    // Seed a pooled model with the mean of the two historical seat fits.
    const weights = Object.fromEntries([...keys].sort().map(key =>
        [key, maps.reduce((total, map) => total + (map[key] ?? 0), 0) / maps.length]));
    return { ...model, version: 2, objective: 'amazoness-complete-duel-v2', weights };
}

function mergeMagistus(board, tactical) {
    if (!tactical || board.version === 'magistus-turn-ranker-v1') return board;
    const { weights, ...tactics } = tactical;
    return { version: 'magistus-turn-ranker-v1', tactics,
        weights: { ...board.weights, ...Object.fromEntries(Object.entries(weights).map(([key, value]) => ['tactics/' + key, value])) } };
}

function baseModels() {
    const json = name => JSON.parse(fs.readFileSync(path.join(__dirname, name)));
    const Amazoness = mergeAmazoness(json('Amazoness.json'));
    const Magistus = mergeMagistus(json('Magistus.first-turn.json'), json('Magistus.second-turn.json'));
    const twins = json('Twins2.second-turn-ranker.json');
    const lines = fs.readFileSync(path.join(__dirname, 'Twins2.first-turn-ranker.tsv'), 'utf8').trimEnd().split('\n');
    if (lines.shift() !== 'twins-branch-ranker-v1') throw new Error('Unsupported Twins frontier format');
    const frontier = Object.fromEntries(lines.map(line => {
        const [key, ...values] = line.split('\t').map(Number);
        if (values.length !== 4 || [...values, key].some(n => !Number.isFinite(n))) throw new Error('Invalid frontier');
        return [key, values];
    }));
    return { Amazoness, Magistus, Twins2: {version:2,weights:twins.weights,frontier} };
}

const modelHash = model => createHash('sha256').update(JSON.stringify(model)).digest('hex');
function calibrationPath(deck) {
    return path.join(__dirname,deck+'.turn-calibration.json');
}
function calibrated(deck, base) {
    const file=calibrationPath(deck);
    if (!fs.existsSync(file)) return base;
    const calibration=JSON.parse(fs.readFileSync(file,'utf8'));
    if (calibration.base!==modelHash(base)) throw Error('Calibration base changed for '+deck);
    const model={...base,weights:{...base.weights,...calibration.weights}};
    if (base.frontier) model.frontier={...base.frontier,...calibration.frontier};
    if (calibration.tieFrontier) model.tieFrontier={...(base.tieFrontier ?? base.frontier),...calibration.tieFrontier};
    return model;
}
function pack(output) {
    fs.mkdirSync(output,{recursive:true});
    for (const [deck,base] of Object.entries(baseModels()))
        fs.writeFileSync(path.join(output,deck+'.turn-ranker.json'),JSON.stringify(calibrated(deck,base))+'\n');
}

if (require.main === module) {
    if (!process.argv[2]) throw new Error('Pass the model output directory');
    pack(path.resolve(process.argv[2]));
}

module.exports = { mergeAmazoness, mergeMagistus, pack, baseModels, modelHash, calibrationPath, calibrated };
