const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const crypto = require('node:crypto');
const P = require('ygopro-msg-encode');
const Deck = require('ygopro-deck-encode').default;

const [port, deckFile] = process.argv.slice(2);
const deckInput = JSON.parse(fs.readFileSync(deckFile));
const deck = new Deck(Array.isArray(deckInput) ? { main: deckInput, extra: [], side: [] } : deckInput);
const second = process.env.TWINS_GO_SECOND === '1';
const opposingMonster = Number(process.env.TWINS_OPPONENT_MONSTER || 0);
const checkOpponentTurn = process.env.TWINS_CHECK_OPPONENT_TURN === '1';
const testMonster = 7572887; // D.D. Warrior Lady supplies a target for removal or effect negation.
const followupMonster = 31305911; // Marshmallon can still use its effects through Gryphon after a Normal Summon.
const testSpells = [75782277, 19523799, 86318356, 44947065, 43422537];
const activatedSpells = new Set();
let summoned = false;
let ashUsed = false;
const ashTarget = Number(process.env.TWINS_ASH_TARGET || 0);
const interference = process.env.TWINS_INTERFERENCE;
const handTraps = { nibiru: 27204311, ash: 14558128, dragon: 70088809, droll: 94145022, ogre: 59438930, imperm: 10045474 };
const master = 72444406;
let interferenceUsed = false;
let interferenceTarget = 0;
let masterStarted = false;
let pendingSummons = 0;
let ownSummons = 0;
let phase = 0;
let firstMainTime;
let firstActionTime;
let lastMainActivity;
let maxMainGap = 0;
let turn = 0;
const activity = new Set(['Summoning', 'SpSummoning', 'Chaining', 'ChainSolved', 'ChainEnd', 'Move', 'Draw']);
const suffix = crypto.randomBytes(4).toString('hex');
const wire = net.connect({ host: '127.0.0.1', port: Number(port) });
const room = (second ? 'NF,NS,LP50000' : 'NF,NS') + '#Twins-' + suffix;
const trace = [];
const monsters = new Map();
let host, ready, started, ended, won, error;
let rps = Number(process.env.TWINS_RPS || '0');
let buffer = Buffer.alloc(0);

function send(type, fields = {}) {
    wire.write(new P[type]().fromPartial(fields).toFullPayload());
}

function chooseHandTrap(game, lastChain) {
    if (!interference || interferenceUsed || turn !== 1 || phase !== 4)
        return undefined;
    const ownEffect = lastChain?.[2] === 0 && lastChain[3] === 4;
    const code = lastChain?.[1];
    const trigger = {
        nibiru: ownSummons === 5,
        ash: ownEffect && code === 2463794,
        dragon: ownEffect && code === 6636319,
        droll: true,
        ogre: ownEffect && code === 4731783,
        imperm: ownEffect && [9205573, 9205574, 36609518, 36609519, 25153, 49867899, 24269961].includes(code),
    }[interference];
    if (!trigger)
        return undefined;
    const chain = game.chains.find(choice => choice.code === handTraps[interference]);
    if (chain) {
        interferenceUsed = true;
        interferenceTarget = ['ash', 'dragon', 'ogre', 'imperm'].includes(interference) ? code : 0;
        trace.push(['Interference', interference, chain.code, interferenceTarget, ownSummons]);
    }
    return chain;
}

function interferenceCards(game) {
    const cards = game.cards;
    const cardCode = card => card.code || monsters.get(card.controller * 8 + card.sequence) || 0;
    if (interference === 'dragon') {
        const extra = cards.filter(card => card.location === 64);
        if (extra.length)
            return [extra.find(card => card.code === master), ...extra.filter(card => card.code !== master)]
                .filter(Boolean).slice(0, game.min === game.max ? game.min : Math.min(5, game.max));
        if (masterStarted) {
            const target = cards.find(card => card.controller === 0 && card.location === 4
                && cardCode(card) && cardCode(card) !== interferenceTarget);
            if (target) {
                trace.push(['MasterTarget', cardCode(target), target.sequence]);
                return [target];
            }
            return cards.slice(0, game.min);
        }
    }
    const alternatives = ({
        9205573: 9205574, 9205574: 9205573, 36609518: 36609519, 36609519: 36609518,
    })[interferenceTarget];
    const target = cards.find(card => card.location === 4
        && (cardCode(card) === interferenceTarget || cardCode(card) === alternatives));
    if (target)
        trace.push(['InterferenceTarget', cardCode(target), target.sequence]);
    return target ? [target] : cards.slice(0, game.min);
}

wire.on('connect', () => {
    send('YGOProCtosPlayerInfo', { name: 'Twin' + suffix });
    send('YGOProCtosJoinGame', { version: 0x1362, pass: room });
});
wire.on('error', reason => { error = reason; });
wire.on('close', () => { if (!won && !error) error = Error('socket closed'); });

const messages = [
    'YGOProStocErrorMsg', 'YGOProStocTypeChange', 'YGOProStocHsPlayerEnter',
    'YGOProStocSelectHand', 'YGOProStocSelectTp', 'YGOProStocGameMsg',
];
wire.on('data', bytes => {
    buffer = Buffer.concat([buffer, bytes]);
    try {
        while (buffer.length >= 3) {
            const end = buffer.readUInt16LE(0) + 2;
            assert.ok(end >= 3);
            if (buffer.length < end) return;
            const packet = buffer.subarray(0, end);
            buffer = buffer.subarray(end);
            const type = messages.find(key => P[key].identifier === packet[2]);
            if (!type) continue;
            const message = new P[type]().fromFullPayload(packet);
            if (type === 'YGOProStocErrorMsg') throw Error(JSON.stringify(message));
            if (type === 'YGOProStocTypeChange') host = message.isHost && message.playerPosition === 0;
            if (type === 'YGOProStocHsPlayerEnter' && message.pos === 1) ready = message.name === '双子';
            if (type === 'YGOProStocSelectHand') {
                const choices = [P.HandResult.SCISSORS, P.HandResult.ROCK, P.HandResult.PAPER];
                send('YGOProCtosHandResult', { res: choices[rps++ % choices.length] });
            }
            if (type === 'YGOProStocSelectTp')
                send('YGOProCtosTpResult', { res: second ? P.TurnPlayerResult.FIRST : P.TurnPlayerResult.SECOND });
            if (type !== 'YGOProStocGameMsg') continue;
            const game = message.msg;
            const name = game.constructor.name.replace('YGOProMsg', '');
            if (turn === 1 && (name === 'Summoning' || name === 'SpSummoning') && game.controller === 0)
                pendingSummons++;
            if (name === 'Summoned' || name === 'SpSummoned') {
                ownSummons += pendingSummons;
                pendingSummons = 0;
            }
            if (name === 'Start') {
                if (game.playerNumber !== (second ? 0 : 1)) throw Error('RETRY_SEAT');
                started = true;
            }
            if (name === 'Retry') throw Error('duel engine rejected a response');
            if (name === 'Chaining')
                trace.push([name, game.code, game.controller, game.location, game.desc, game.chainCount]);
            else if (['ChainSolving', 'ChainSolved', 'ChainDisabled', 'ChainNegated', 'ChainEnd'].includes(name))
                trace.push([name, game.chainCount]);
            else if (['NewTurn', 'Summoning', 'SpSummoning', 'Draw', 'Retry', 'Attack', 'Damage', 'Win'].includes(name))
                trace.push([name, game.code, game.player]);
            if (name === 'NewTurn') turn++;
            if (name === 'NewPhase' || activity.has(name) || name === 'Win') {
                const now = performance.now();
                if (lastMainActivity != null)
                    maxMainGap = Math.max(maxMainGap, now - lastMainActivity);
                if (name === 'NewPhase')
                    lastMainActivity = turn === (second ? 2 : 1) && [4, 256].includes(game.phase) ? now : null;
                else if (lastMainActivity != null)
                    lastMainActivity = name === 'Win' ? null : now;
            }
            if (name === 'NewPhase') {
                phase = game.phase;
                trace.push([name, game.phase]);
                if (game.phase === 4 && turn === (second ? 2 : 1) && firstMainTime == null)
                    firstMainTime = performance.now();
            }
            if (firstMainTime != null && firstActionTime == null &&
                (name === 'Summoning' || name === 'SpSummoning' || name === 'Chaining')) {
                firstActionTime = Math.round(performance.now() - firstMainTime);
                trace.push(['FirstActionMs', firstActionTime]);
            }
            if (name === 'Move') {
                trace.push([name, game.code, game.previous.location, game.current.location, game.current.sequence,
                            game.previous.controller, game.previous.sequence, game.current.controller]);
                if (game.previous.location === 4)
                    monsters.delete(game.previous.controller * 8 + game.previous.sequence);
                if (game.current.location === 4 && game.code)
                    monsters.set(game.current.controller * 8 + game.current.sequence, game.code);
            }
            if (name === 'SelectChain') {
                const lastChain = trace.findLast(event => event[0] === 'Chaining');
                const ash = ashTarget && !ashUsed && lastChain?.[1] === ashTarget
                    && game.chains.find(chain => chain.code === 14558128);
                if (ash) ashUsed = true;
                const masterEffect = interference === 'dragon' && interferenceUsed && turn === 1
                    && game.chains.find(chain => chain.code === master);
                if (masterEffect) masterStarted = true;
                const selected = game.chains.find(chain => chain.forced)
                    || ash || masterEffect || chooseHandTrap(game, lastChain);
                send('YGOProCtosResponse', { response: selected ? game.prepareResponse(selected) : game.prepareResponse() });
            }
            if (interferenceUsed && turn === 1 && name === 'SelectCard')
                send('YGOProCtosResponse', { response: game.prepareResponse(interferenceCards(game)) });
            if (interference === 'dragon' && interferenceUsed && turn === 1 && name === 'SelectUnselectCard') {
                const choice = game.selectableCards.find(card => card.code === master) || game.selectableCards[0];
                const finish = game.finishable && game.unselectableCards.length >= 5;
                send('YGOProCtosResponse', { response: game.prepareResponse(finish ? null : choice) });
            }
            if (interference === 'dragon' && interferenceUsed && turn === 1
                && (name === 'SelectEffectYn' || name === 'SelectYesNo')) {
                if (name === 'SelectEffectYn' && game.code === master)
                    masterStarted = true;
                send('YGOProCtosResponse', { response: game.prepareResponse(true) });
            }
            if (interferenceUsed && turn === 1 && name === 'SelectOption') {
                send('YGOProCtosResponse', { response: game.prepareResponse(P.IndexResponse(0)) });
            }
            if (name === 'SelectPlace')
                send('YGOProCtosResponse', { response: game.prepareResponse(game.getSelectablePlaces().slice(0, game.count || 1)) });
            if (name === 'SelectPosition')
                send('YGOProCtosResponse', { response: game.prepareResponse(game.positions & -game.positions) });
            if (name === 'SelectIdleCmd') {
                const turn = trace.filter(event => event[0] === 'NewTurn').length;
                if ((second && turn === 1) || (checkOpponentTurn && turn === 2)) {
                    const monster = second ? opposingMonster : testMonster;
                    const target = !summoned && game.summonableCards.find(card => card.code === monster);
                    const spell = checkOpponentTurn && testSpells.filter(code => !activatedSpells.has(code))
                        .map(code => game.activatableCards.find(card => card.code === code)).find(Boolean);
                    const followup = checkOpponentTurn && game.summonableCards.find(card => card.code === followupMonster);
                    if (target) {
                        summoned = true;
                        send('YGOProCtosResponse', { response: game.prepareResponse(P.IdleCmdType.SUMMON, target) });
                    } else if (spell) {
                        activatedSpells.add(spell.code);
                        send('YGOProCtosResponse', { response: game.prepareResponse(P.IdleCmdType.ACTIVATE, spell) });
                    } else if (followup) {
                        send('YGOProCtosResponse', { response: game.prepareResponse(P.IdleCmdType.SUMMON, followup) });
                    } else {
                        send('YGOProCtosResponse', { response: game.prepareResponse(P.IdleCmdType.TO_EP) });
                    }
                } else {
                    if (turn !== (second ? 3 : 2)) throw Error('RETRY_SEAT');
                    ended = true;
                }
            }
            if (checkOpponentTurn && name === 'NewTurn' && turn === 3) ended = true;
            if (name === 'Win') won = true;
        }
    } catch (reason) {
        error = reason;
    }
});

async function until(label, condition) {
    const deadline = Date.now() + 45000;
    while (!condition()) {
        if (error) throw error;
        if (Date.now() >= deadline) throw Error(label + ' timed out');
        await new Promise(resolve => setTimeout(resolve, 20));
    }
}

(async () => {
    await until('lobby', () => host);
    send('YGOProCtosChat', { msg: '/AI 双子' });
    await until('bot joined', () => ready);
    send('YGOProCtosUpdateDeck', { deck });
    send('YGOProCtosHsReady');
    await new Promise(resolve => setTimeout(resolve, 350));
    send('YGOProCtosHsStart');
    await until('duel started', () => started);
    await until('bot finished its turn', () => ended || won);
    if (!won) {
        send('YGOProCtosSurrender');
        await until('duel ended', () => won);
    }
    trace.push(['MaxMainGapMs', Math.round(maxMainGap)]);
    console.log(JSON.stringify(trace));
})().catch(reason => {
    console.error(reason.message);
    console.error(JSON.stringify(trace));
    process.exitCode = 1;
}).finally(() => wire.destroy());
