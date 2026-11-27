'use strict';

const fs = require('node:fs');
const { createHash } = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');
const P = require('ygopro-msg-encode');
const coreApi = require('koishipro-core.js');
const { engineOptions, nameOf, UnsupportedPrompt } = require('../tests/engine-search-protocol.cjs');
const { twinsTie } = require('../turn-evidence.cjs');
const { compareTurn, turnAxis, verifyTiedRoutes } = require('../turn-objective.cjs');
const { twinsPolicy, position } = require('../tests/twins-engine-reference.cjs');

const BOT = 1, OPPONENT = 0;
const MAIN1 = 4, MAIN2 = 256;
const MONSTER = 4, SPELL = 8;
const pressure = new Set([14558128, 94145022, 27204311, 59438930, 10045474, 73642296, 24508238, 33854624]);
const removal = new Set([36609518, 36609519, 93672138, 4731783, 82135803, 25153]);
const botPolicy = {
    ...twinsPolicy,
    includeSequence: true,
    choicePriority(card, state) {
        if (state.solving?.player === BOT && card.controller === OPPONENT
            && (card.location === MONSTER || card.location === SPELL))
            return 24;
        return twinsPolicy.choicePriority(card, state);
    },
};

function initialDuel(core, input) {
    const duel = core.createDuelV2([input.seed >>> 0, 0, 0, 0, 0, 0, 0, 0]);
    duel.loadRegistry({ duel_mode: 'single', start_lp: '8000', start_hand: '5', draw_count: '1' });
    for (const script of ['./script/patches/entry.lua', './script/special.lua', './script/init.lua'])
        duel.preloadScript(script);
    [input.opponent, input].forEach((side, player) => {
        duel.setPlayerInfo({ player, lp: player === BOT ? 8000 : input.opponent_lp, startHand: 5, drawCount: 1 });
        for (const [cards, location] of [[side.main, 1], [side.extra, 64]])
            for (const code of [...cards].reverse())
                duel.newCard({ code, owner: player, player, location, sequence: 0, position: 8 });
    });
    for (const card of input.field)
        duel.newCard({ ...card, owner: OPPONENT, player: OPPONENT });
    duel.startDuel((5 << 16) | coreApi.OcgcoreDuelOptionFlag.PseudoShuffle);
    return duel;
}

function field(duel, player, queryFlag = 0x30f | 0x4000) {
    return [MONSTER, SPELL].flatMap(location => duel.queryFieldCard({
        player, location, queryFlag, useCache: 0,
    }).cards.map((card, sequence) => ({ ...card, sequence, location })).filter(card => card.code));
}

function stateFeatures(duel, seat = BOT) {
    const own = field(duel, seat);
    const enemy = field(duel, 1-seat);
    return {
        bodies: own.filter(card => card.location === MONSTER).length,
        enemy: enemy.filter(card => card.location === MONSTER).length,
        backrow: enemy.filter(card => card.location === SPELL).length,
        attack: own.reduce((total,card)=>total+(card.location===MONSTER ? Math.max(0,card.attack ?? 0) : 0),0),
        // Mask hidden identities. Location and position are public.
        enemyBoard: enemy.filter(card => card.position & 5).map(card => card.code).sort((a, b) => a - b),
    };
}

const token = value => Buffer.from(value).toString('base64');
function choices(msg, state) {
    const name = nameOf(msg);
    if (name === 'SelectBattleCmd') {
        const C = P.BattleCmdType;
        const result = msg.attackableCards.map((card, i) => ({
            key: `attack:${card.code}:${card.sequence}:${card.directAttack}`,
            weight: 35, response: msg.prepareResponse(C.ATTACK, P.IndexResponse(i)),
        }));
        result.push(...msg.activatableCards.map((card, i) => ({
            key: `battle-effect:${card.code}:${card.desc}`, weight: removal.has(card.code) ? 26 : 8,
            response: msg.prepareResponse(C.ACTIVATE, P.IndexResponse(i)),
        })));
        if (msg.canM2) result.push({ key: 'main2', weight: 3, response: msg.prepareResponse(C.TO_M2) });
        return result;
    }
    const view = ['SelectCard', 'SelectUnselectCard', 'SelectTribute'].includes(name)
        && typeof msg.playerView === 'function' ? msg.playerView(BOT) : msg;
    const result = engineOptions(view, state, botPolicy);
    if (name === 'SelectIdleCmd' && state.phase === MAIN1 && msg.canBp) {
        return result.map(option =>
            option.key === 'battle' ? { ...option, weight: state.field.size ? 18 : 1 } : option);
    }
    return result;
}

function enemyResponse(msg, state) {
    const name = nameOf(msg);
    if (name === 'SelectIdleCmd')
        return msg.prepareResponse(P.IdleCmdType.TO_EP);
    if (name === 'SelectChain') {
        const forced = msg.chains.find(card => card.forced);
        if (forced) return msg.prepareResponse(forced);
        const prior = state.trace.findLast(e => e[0] === 'Chaining');
        const active = state.chains.size !== 0 && prior?.[2] === 0;
        const choice = state.turn === 2 && msg.chains.find(card => {
            if (!pressure.has(card.code) || state.tried.has(card.code)) return false;
            if (card.code === 27204311) return state.summons >= 5;
            if (card.code === 94145022 || card.code === 24508238 || card.code === 33854624) return true;
            if (card.code === 73642296) return active && prior[1] === 93672138;
            return active && (card.code !== 59438930 || prior[3] === MONSTER);
        });
        if (choice) state.tried.add(choice.code);
        return msg.prepareResponse(choice || undefined);
    }
    if (name === 'SelectCard') {
        const targets = [...msg.cards].sort((a,b) => Number(b.controller === BOT) - Number(a.controller === BOT));
        return msg.prepareResponse(targets.slice(0, msg.min));
    }
    if (name === 'SelectEffectYn' || name === 'SelectYesNo') return msg.prepareResponse(false);
    if (name === 'SelectPosition') {
        const allowed = msg.positions & -msg.positions;
        return msg.prepareResponse(allowed);
    }
    return engineOptions(msg, state, botPolicy)[0]?.response;
}

function update(msg, state, duel) {
    const name = nameOf(msg);
    if (name === 'NewTurn') state.turn++;
    if (name === 'Damage') state.lp[msg.player] -= msg.value;
    if (name === 'PayLpCost') state.lp[msg.player] -= msg.cost;
    if (name === 'Recover') state.lp[msg.player] += msg.value;
    if (name === 'NewPhase') {
        state.phase = msg.phase;
        if (state.turn === 2 && msg.phase === MAIN1) state.before = field(duel, OPPONENT).length;
        if (state.turn === 2 && msg.phase === MAIN2) state.remaining = field(duel, OPPONENT).length;
    }
    if (state.turn !== 2) return;
    if (name === 'Damage' && msg.player === OPPONENT && state.remaining === undefined)
        state.damage += msg.value;
    if (name === 'Chaining') {
        state.chains.set(msg.chainCount, { code: msg.code, player: msg.controller });
        state.trace.push([name, msg.code, msg.controller === BOT ? 0 : 1, msg.location, msg.desc]);
    }
    if (name === 'ChainSolving') state.solving = state.chains.get(msg.chainCount);
    if (name === 'ChainDisabled' || name === 'ChainNegated') state.chains.delete(msg.chainCount);
    if (name === 'ChainSolved') {
        const effect = state.chains.get(msg.chainCount);
        if (effect?.player === OPPONENT && pressure.has(effect.code)) {
            state.interrupted++;
            if (effect.code === 94145022) state.searchLocked = true;
        }
        state.solving = undefined;
    }
    if (name === 'ChainEnd') state.chains.clear();
    if (name === 'Summoning' || name === 'SpSummoning') {
        if (msg.controller === BOT) state.pending++;
        if (name === 'Summoning' && msg.controller === BOT) {
            state.normal.add(msg.sequence);
            state.normalUsed = true;
        }
    }
    if (name === 'Summoned' || name === 'SpSummoned') {
        state.summons += state.pending;
        state.pending = 0;
    }
    if (name === 'Hint' && msg.player === BOT && msg.type === 3) state.selectionHint = msg.desc;
    if (name === 'Draw' && msg.player === BOT) {
        state.trace.push([name, null, 0]);
        if ([9205573, 9205574, 54257392, 65330383].includes(state.solving?.code)) state.scoringDraws++;
    }
    if (name !== 'Move') return;
    if (msg.previous.controller === BOT && msg.previous.location === MONSTER) {
        state.field.delete(msg.previous.sequence);
        state.normal.delete(msg.previous.sequence);
    }
    if (msg.current.controller === BOT && msg.current.location === MONSTER)
        state.field.set(msg.current.sequence, twinsPolicy.normalize(msg.code));
    if (msg.previous.controller === BOT && msg.previous.location === 16) {
        const i = state.grave.indexOf(twinsPolicy.normalize(msg.code));
        if (i >= 0) state.grave.splice(i, 1);
    }
    if (msg.current.controller === BOT && msg.current.location === 16) state.grave.push(twinsPolicy.normalize(msg.code));
    if (msg.previous.controller === OPPONENT && [MONSTER, SPELL].includes(msg.previous.location)
        && (msg.current.controller !== OPPONENT || ![MONSTER, SPELL].includes(msg.current.location))
        && state.solving?.player === BOT)
        state.effectClears.push(state.solving.code);
}

function weighted(options, random) {
    let ticket = random() * options.reduce((sum, item) => sum + item.weight, 0);
    return options.find(item => (ticket -= item.weight) < 0) || options.at(-1);
}

function features(state, own, prompt, key, seat = BOT) {
    return [`a:${prompt}:${key}`, `phase:${state.phase}:${key}`,
        `bodies:${Math.min(4,own.bodies)}:${key}`, `enemy:${Math.min(4,own.enemy)}:${key}`,
        `backrow:${Math.min(4,own.backrow)}:${key}`,
        `atk:${Math.min(8,Math.floor(own.attack/1000))}:${key}`,
        `their-lp:${Math.min(8,Math.max(0,Math.ceil(state.lp[1-seat]/1000)))}:${key}`,
        `our-lp:${Math.min(8,Math.max(0,Math.ceil(state.lp[seat]/1000)))}:${key}`,
        `post-hit:${state.interrupted > 0}:${key}`, `normal:${state.normalUsed}:${key}`,
        ...own.enemyBoard.map(id => `visible:${id}:${key}`)];
}

function rollout(core, input, random, deadline, parent, pivot) {
    const duel = initialDuel(core, input);
    const state = { turn: 0, phase: 0, field: new Map(), grave: [], normal: new Set(), trace: [],
        chains: new Map(), tried: new Set(), effectClears: [], damage: 0, interrupted: 0,
        summons: 0, pending: 0, scoringDraws: 0, normalUsed: false, lp: [input.opponent_lp, 8000],
        declarations: [...new Set(input.main)] };
    const wire = [], steps = [];
    let follow = 1;
    const terminal = (win, remaining) => {
        const board = win ? null : position(duel, state, BOT);
        const tiebreak = board && twinsTie(board, { hardHit: state.interrupted > 0 });
        const removed = (state.before ?? input.field.length) - remaining;
        return { outcome: [win, removed, state.damage, 0, tiebreak?.score ?? 0, removed],
            tiebreak, main2: state.remaining !== undefined, board, interrupted: state.interrupted,
            effectClears: state.effectClears, wire, steps };
    };
    try {
        for (let i = 0; i < 1800 && performance.now() < deadline; i++) {
            for (const msg of duel.process().messages || []) {
                const name = nameOf(msg);
                if (name === 'Retry') return null;
                update(msg, state, duel);
                if (name === 'Win') {
                    if (msg.player === BOT) return terminal(1, field(duel, OPPONENT).length);
                    if (msg.player === OPPONENT) return terminal(-1, field(duel, OPPONENT).length);
                    return null;
                }
                if (state.turn === 3) return terminal(0, field(duel, OPPONENT).length);
                if (!(msg instanceof P.YGOProMsgResponseBase)) continue;
                const offset = wire.length;
                const previous = parent && offset < pivot.offset ? parent.wire[offset] : undefined;
                if (msg.responsePlayer() !== BOT || state.turn !== 2) {
                    const response = previous === undefined ? enemyResponse(msg,state) : Buffer.from(previous,'base64');
                    if (response == null) return null;
                    if (previous !== undefined && name === 'SelectChain') {
                        const option = msg.chains.find((card, i) => token(msg.prepareResponse(P.IndexResponse(i))) === previous);
                        if (option && pressure.has(option.code)) state.tried.add(option.code);
                    }
                    wire.push(token(response));
                    duel.setResponse(response);
                    continue;
                }
                const options = choices(msg, state);
                if (!options.length) return null;
                let choice;
                if (previous !== undefined) {
                    choice = options.find(a => token(a.response) === previous);
                    if (!choice) return null;
                } else if (parent && offset === pivot.offset) {
                    const alternatives = options.filter(a => token(a.response) !== parent.wire[offset]);
                    choice = weighted(alternatives.length ? alternatives : options, random);
                } else if (parent && options.length > 1) {
                    const next = parent.steps.slice(pivot.index+follow,pivot.index+follow+6);
                    const match = next.findIndex(s => s.name === name && options.some(a => a.key === s.key));
                    if (match >= 0) {
                        choice = options.find(a => a.key === next[match].key);
                        follow += match+1;
                    }
                }
                choice ||= weighted(options,random);
                if (options.length > 1 && ['SelectIdleCmd','SelectBattleCmd','SelectCard','SelectChain',
                    'SelectUnselectCard','SelectEffectYn'].includes(name)) {
                    const prefix=createHash('sha256').update(wire.join('|')).digest('hex').slice(0,20);
                    steps.push({name,key:choice.key,offset,index:steps.length,prefix,action:token(choice.response),
                        features:features(state,stateFeatures(duel),name,choice.key)});
                }
                if (name === 'SelectIdleCmd') state.lastIdle = choice.card;
                if (name === 'SelectIdleCmd' && choice.key.startsWith('0/')) state.normalUsed=true;
                wire.push(token(choice.response));
                duel.setResponse(choice.response);
            }
        }
        return null;
    } finally {
        duel.endDuel();
    }
}

const compare = compareTurn;

function buildPairs(runs) {
    const groups=new Map();
    for (const r of runs) for (const step of r.steps) {
        if (!groups.has(step.prefix)) groups.set(step.prefix,new Map());
        const choices=groups.get(step.prefix);
        const old=choices.get(step.action);
        if (!old || compare(r.outcome,old.outcome)>0)
            choices.set(step.action,{features:step.features,outcome:r.outcome,tiebreak:r.tiebreak,tieVerified:r.tieVerified});
    }
    const pairs=[];
    for (const [prefix, choices] of groups) {
        const lines=[...choices.values()].sort((a,b)=>compare(b.outcome,a.outcome));
        const high=lines[0];
        for (const low of lines.slice(1)) {
            const axis=turnAxis(high.outcome, low.outcome);
            if (axis>=0 && (axis!==4 || high.tieVerified && low.tieVerified) &&
                compare(high.outcome,low.outcome)>0 && high.features.join('|')!==low.features.join('|'))
                pairs.push({ prefix, high:high.features, low:low.features, axis,
                    ...(axis === 4 && {tiebreak: { high:high.tiebreak, low:low.tiebreak }}) });
        }
    }
    return pairs;
}

async function main() {
    const [fixture,caseFile]=process.argv.slice(2);
    const {input,attempts,budget,scorer}=JSON.parse(fs.readFileSync(caseFile));
    const {CardLoadWorker}=require(fixture+'/dist/src/ygopro/card-load-worker');
    const paths=[fixture+'/ygopro'];
    const {cardStorage,failedFiles}=await new CardLoadWorker(paths).load();
    if (failedFiles.length) throw new Error('could not load card data: '+failedFiles.join(', '));
    const wasm=cardStorage.ocgcoreWasmBinary;
    const core=await coreApi.createOcgcoreWrapper(wasm ? {wasmBinary:wasm} : undefined);
    core.setCardReader(cardStorage.toCardReader());
    core.setScriptReader(await coreApi.DirScriptReaderEx(...paths));
    const deadline=performance.now()+budget;
    let seed=input.seed>>>0;
    const random=()=> (seed=(Math.imul(seed,1664525)+1013904223)>>>0)/0x100000000;
    const runs=[], unsupported={};
    let pool=[];
    let completed=0;
    for (;completed<attempts && performance.now()<deadline;completed++) {
        const parent=completed%5 && pool.length ? pool[Math.floor(random()**2*pool.length)] : null;
        const pivot=parent?.steps[Math.floor(random()*parent.steps.length)];
        try {
            const run=rollout(core,input,random,deadline,pivot ? parent : null,pivot);
            if (!run) continue;
            runs.push(run);
            if (run.steps.length) pool=[...pool,run].sort((a,b)=>compare(b.outcome,a.outcome)).slice(0,24);
        } catch (error) {
            if (!(error instanceof UnsupportedPrompt)) throw error;
            unsupported[error.prompt]=(unsupported[error.prompt]||0)+1;
        }
    }
    const settled=runs.filter(r=>r.outcome[0]===0);
    function scoreMain2(terminals) {
        if (!terminals.length) return;
        const [command,...args]=scorer;
        const result=spawnSync(command,[...args,fixture+'/cards.cdb'],{
            input:terminals.map(r=>JSON.stringify({position:r.board})).join('\n')+'\n',encoding:'utf8',timeout:8000,
        });
        if (result.status!==0) throw new Error(result.stderr||String(result.error));
        const scores=result.stdout.trim().split(/\r?\n/).map(Number);
        if (scores.length!==terminals.length||scores.some(x=>!Number.isFinite(x))) throw new Error('invalid main2 scores');
        terminals.forEach((r,i)=>{ r.outcome[3]=scores[i]; r.outcome[5]=r.outcome[1]+scores[i]; });
    }
    scoreMain2(settled);
    verifyTiedRoutes(settled, route => {
        const candidate = rollout(core, input, random, performance.now() + 350, route, {offset:Infinity});
        if (candidate?.outcome[0] === 0) scoreMain2([candidate]);
        return candidate;
    });
    const best=[...runs].sort((a,b)=>compare(b.outcome,a.outcome))[0];
    const replay = best && rollout(core,input,random,performance.now()+3000,best,{offset:Infinity});
    if (replay?.outcome[0] === 0) scoreMain2([replay]);
    const replayVerified = Boolean(replay && best.outcome.every((n,i)=>n===replay.outcome[i])
        && best.main2===replay.main2 && best.interrupted===replay.interrupted);
    const witness=best? {outcome:best.outcome,interrupted:best.interrupted,effectClears:best.effectClears,
        main2:best.main2,wire:best.wire}:null;
    console.log(JSON.stringify({attempts:completed,settled:runs.length,wins:runs.filter(r=>r.outcome[0]===1).length,
        main2:settled.length,interrupted:runs.filter(r=>r.interrupted).length,
        effects:runs.filter(r=>r.effectClears.length).length,unsupported,witness,replayVerified,
        pairs:replayVerified ? buildPairs(runs) : []}));
}

if (require.main === module)
    main().catch(error=>{console.error(error.stack);process.exitCode=1;});

module.exports = { initialDuel, field, buildPairs, compare, stateFeatures, features };
