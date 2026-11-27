'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseArgs } = require('node:util');
const P = require('ygopro-msg-encode');
const { MagistusPolicy } = require('../magistus/live-policy.cjs');
const { AmazonessPolicy } = require('../amazoness/live-policy.cjs');
const { createDuelFromYrp } = require('koishipro-core.js');
const { YGOProYrp, ReplayHeader } = require('ygopro-yrp-encode');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { nameOf } = require('./engine-search-protocol.cjs');
const { openWindBot } = require('./replay-windbot-seat.cjs');

const cases = {
    magistus: { seat: 1, turn: 2, phase: 4, pivot: 'SelectIdleCmd', damage: [], tribute: [44001993, 27519978],
        board: [24203749, 27548199], equip: 34755994 },
    amazoness: { seat: 1, turn: 2, phase: 4, pivot: 'SelectCard', damage: [400, 1500, 400, 1900, 1500, 2600], win: true },
    twins: { seat: 0, turn: 3, phase: 4, pivot: 'SelectIdleCmd', damage: [200, 200, 3300, 1100, 1100, 2500], win: true },
    'twins-scout': { seat: 0, turn: 3, phase: 4, pivot: 'SelectIdleCmd', damage: [800, 200, 200],
        scout: true },
};

function openDuel(core, fixture) {
    const { seed, startLp, startHand, drawCount, opt, decks } = fixture;
    const header = new ReplayHeader();
    header.seedSequence = seed;
    const replay = new YGOProYrp({ header, startLp, startHand, drawCount, opt,
        hostDeck: decks[0], clientDeck: decks[1], responses: [] });
    return createDuelFromYrp(core, replay);
}

async function runWindBot(core, fixture, exe) {
    const diagnostics = [];
    const seat = await openWindBot(exe, fixture, line => diagnostics.push(line));
    const { duel } = openDuel(core, fixture);
    const prefix = fixture.prefix.split(' ');
    let offset = 0;
    const damage = [], decisions = [];
    try {
        for (let spin = 0; spin < 2000; spin++) {
            for (const { message } of duel.splitProcessResult(duel.process())) {
                if (!message) continue;
                const name = nameOf(message);
                if (name === 'Retry') throw new Error('WindBot sent an illegal response');
                if (name === 'Win') {
                    assert.equal(message.player, 0);
                    assert.equal(damage.reduce((sum, hit) => sum + hit, 0), 8400);
                    return { turn: 3, damage: 8400, win: true };
                }
                if (message instanceof P.YGOProMsgResponseBase) {
                    if (offset < prefix.length) {
                        duel.setResponse(Buffer.from(prefix[offset++], 'hex'));
                        continue;
                    }
                    let reply;
                    if (message.responsePlayer() === 1) {
                        if (name === 'SelectChain') reply = message.prepareResponse(message.chains.find(c => c.forced));
                        if (name === 'SelectEffectYn') reply = message.prepareResponse(message.code === 72270339);
                        if (name === 'SelectCard') reply = message.prepareResponse(message.cards.filter(c => c.location === 8).slice(0, 1));
                        if (name === 'SelectPlace') reply = message.prepareResponse(message.getSelectablePlaces().slice(0, 1));
                        if (name === 'SelectPosition') reply = message.prepareResponse(4);
                        assert(reply, 'Unexpected opponent prompt ' + name);
                    } else if (name === 'SelectChain' && !message.chains.length) {
                        reply = message.prepareResponse();
                    } else {
                        for (const player of [0, 1]) for (const location of [4, 8, ...(player === 0 ? [2, 16, 64] : [])]) {
                            const cards = duel.queryFieldCard({ player, location, queryFlag: 0xfbffff }).cards;
                            seat.onMessage(new P.YGOProMsgUpdateData().fromPartial({ player, location, cards }));
                        }
                        reply = await seat.choose(message);
                        decisions.push([name, reply.toString('hex')]);
                    }
                    duel.setResponse(reply);
                } else {
                    seat.onMessage(message);
                    if (offset >= prefix.length && name === 'Damage' && message.player === 1) damage.push(message.value);
                }
            }
        }
        throw new Error('WindBot replay did not reach a terminal');
    } catch (error) {
        console.error(decisions.slice(-30), diagnostics.join('').split('\n').slice(-45).join('\n'));
        throw error;
    } finally {
        seat.close();
        duel.endDuel();
    }
}

function run(core, fixture, expected, policy) {
    const { duel } = openDuel(core, fixture);
    const { startLp } = fixture;
    const prefix = fixture.prefix.split(' ');
    assert.equal(prefix.length, fixture.pivot);
    let index = 0, turn = 0, phase = 0, terminal = false, closingBoard;
    const damage = [], phases = [], tributes = [], summoned = [];
    let opponentDefended = false;
    try {
        for (const { message } of duel.advance(prompt => {
            const name = nameOf(prompt);
            if (index < prefix.length) return Buffer.from(prefix[index++], 'hex');
            if (index === prefix.length) {
                assert.equal(turn, expected.turn);
                assert.equal(phase, expected.phase);
                assert.equal(name, expected.pivot);
                assert.equal(prompt.responsePlayer(), expected.seat);
            }
            if (policy) {
                index++;
                if (prompt.responsePlayer() === expected.seat) {
                    if (name === 'SelectChain' && !prompt.chains.length)
                        return prompt.prepareResponse();
                    return policy.choose(prompt);
                }
                if (name === 'SelectChain') return prompt.prepareResponse(prompt.chains.find(c =>
                    c.forced || expected.win && [77456448, 42141493].includes(c.code)));
                if (name === 'SelectOption' && prompt.count === 1)
                    return prompt.prepareResponse(P.IndexResponse(0));
                throw new Error('Unexpected opponent prompt ' + name);
            }
            const entry = fixture.continuation[index++ - prefix.length];
            assert.ok(entry, 'Reference continuation stopped before its terminal state: ' + name);
            assert.equal(name, entry.name);
            assert.equal(prompt.responsePlayer(), entry.player);
            return Buffer.from(entry.response, 'hex');
        })) {
            if (!message) continue;
            const name = nameOf(message);
            assert.notEqual(name, 'Retry', 'Engine rejected response ' + (index - 1));
            if (policy) {
                policy.observe(message.playerView?.(expected.seat) ?? message);
                if (message instanceof P.YGOProMsgResponseBase && index >= prefix.length) {
                    // The room sends query updates alongside engine notifications.
                    for (const player of [0, 1]) for (const location of [4, 8, ...(player === expected.seat ? [2, 16] : [])]) {
                        const cards = duel.queryFieldCard({ player, location, queryFlag: 0x8c35f }).cards;
                        const visible = player === expected.seat ? cards : cards.map(c => c.position & 5 || c.empty ?
                            c : { ...c, code: 0, alias: 0, attack: undefined, defense: undefined });
                        const update = new P.YGOProMsgUpdateData().fromPartial({ player, location, cards: visible });
                        policy.observe(update.playerView?.(expected.seat) ?? update);
                    }
                }
            }
            if (name === 'NewTurn') turn++;
            if (name === 'NewPhase') phase = message.phase;
            if (index < prefix.length) continue;
            if (name === 'NewPhase') phases.push(phase);
            if (name === 'Move' && message.previous.controller !== expected.seat &&
                message.previous.location === 4 && message.current.location === 16 && message.reason & 2)
                tributes.push(message.code);
            if (name === 'Damage' && message.player !== expected.seat) damage.push(message.value);
            if (name === 'Summoning' || name === 'SpSummoning') {
                if (message.controller === expected.seat) summoned.push(message.code);
                else if (message.code === 72270339 && message.position === 4) opponentDefended = true;
            }
            if (name === 'NewPhase' && phase === 512 && expected.board)
                closingBoard = duel.queryFieldCard({ player: expected.seat, location: 4, queryFlag: 0x1 | 0x4000 }).cards;
            if (name === 'Win' || name === 'NewTurn' && turn > expected.turn) {
                if (expected.win) {
                    assert.equal(name, 'Win');
                    assert.equal(message.player, expected.seat);
                } else {
                    assert.equal(name, 'NewTurn');
                    if (!expected.scout)
                        assert.equal(duel.queryFieldInfo().field.players[expected.seat].lp, startLp);
                }
                terminal = true;
                break;
            }
        }
        assert(terminal, 'Replay ended without reaching the expected result');
        if (!policy) assert.equal(index, prefix.length + fixture.continuation.length);
        assert.deepEqual([...damage].sort(), [...expected.damage].sort());
        if (expected.tribute) assert.deepEqual(tributes, expected.tribute);
        if (expected.board) {
            assert(!phases.includes(8), 'Main-phase route entered battle');
            assert.deepEqual(closingBoard.map(c => c.code).filter(Boolean).sort(), [...expected.board].sort());
            const spells = duel.queryFieldCard({ player: expected.seat, location: 8, queryFlag: 1 }).cards;
            assert(spells.some(c => c.code === expected.equip));
        }
        if (expected.scout) {
            const cards = (player, location) => duel.queryFieldCard({ player, location, queryFlag: 1 }).cards.map(c => c.code);
            assert(!cards(1, 4).filter(Boolean).length);
            assert(cards(1, 16).includes(72270339));
            assert(cards(0, 4).includes(25154));
            assert(cards(0, 8).includes(98360333));
            assert(cards(0, 8).includes(25156));
        } else if (expected.seat === 0) {
            assert(opponentDefended);
            assert.deepEqual(summoned.filter(code => [36609519, 9205574, 65330383, 25154, 93672138].includes(code)),
                [36609519, 9205574, 65330383, 36609519, 25154, 93672138, 36609519, 9205574]);
        }
        return { turn: expected.turn, damage: damage.reduce((sum, hit) => sum + hit, 0), win: !!expected.win };
    } finally {
        duel.endDuel();
    }
}

async function main() {
    const { values } = parseArgs({ options: { fixture: { type: 'string' }, data: { type: 'string', multiple: true }, windbot: { type: 'string' }, models: { type: 'string' } } });
    assert(values.fixture && values.data?.length, 'Require --fixture and one or more --data paths');
    const engine = await loadEngine(path.resolve(values.fixture), values.data.map(v => path.resolve(v)));
    try {
        for (const [deck, expected] of Object.entries(cases)) {
            const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/turn-policy', deck + '.json')));
            const result = run(engine.core, fixture, expected);
            assert.deepEqual([...engine.engineErrors], [], 'Engine script errors in ' + deck);
            console.log(JSON.stringify({ deck, mode: 'reference', ...result }));
            if (deck === 'twins' && values.windbot) {
                const live = await runWindBot(engine.core, fixture, path.resolve(values.windbot));
                console.log(JSON.stringify({ deck, mode: 'policy', ...live }));
            }
            if (deck === 'magistus' || deck === 'amazoness') {
                const events = [];
                const extra = fixture.decks[expected.seat].extra;
                const log = event => events.push(event);
                const load = name => JSON.parse(fs.readFileSync(path.join(values.models, name)));
                const policy = deck === 'amazoness' ?
                    new AmazonessPolicy(expected.seat, engine.cardReader,
                        values.models ? load('Amazoness.turn-ranker.json') : require('../Models/Amazoness.json'), log, extra) :
                    new MagistusPolicy(expected.seat, engine.cardReader, require('../magistus/live-guides.json'),
                        values.models ? load('Magistus.turn-ranker.json') : require('../Models/Magistus.first-turn.json'),
                        log, extra, values.models ? undefined : require('../Models/Magistus.second-turn.json'));
                try {
                    const live = run(engine.core, fixture, expected, policy);
                    assert(events.some(e => e.event === (deck === 'amazoness' ? 'amazoness-lethal-plan' : 'magistus-outlaw-plan')));
                    console.log(JSON.stringify({ deck, mode: 'policy', ...live }));
                } catch (error) {
                    console.error(events.slice(-35));
                    throw error;
                }
            }
        }
    } finally {
        engine.core.finalize();
    }
}

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { run };
