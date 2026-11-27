'use strict';

// Host-side test client for an isolated SRVPro2 fixture.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const crypto = require('node:crypto');
const P = require('ygopro-msg-encode');
const Deck = require('ygopro-deck-encode').default;
const { nameOf } = require('../tests/engine-search-protocol.cjs');
const { MagistusPolicy } = require('./live-policy.cjs');

const [port, which = 'named', host = '127.0.0.1', flags = 'NC,NS'] = process.argv.slice(2);
const name = which === 'pool' ? '双子' : '大贤者';
const hostFirst = ['second', 'second-battle', 'token'].includes(which);
const battleMode = which === 'second-battle';
const ELF = 15025844;
const socket = net.connect({ host, port: Number(port) });
const trap = which === 'nibiru' ? 27204311 : which === 'maxx' ? 23434538 : null;
const fixtureDeck = { main: trap ? [trap, ...Array(39).fill(89631139)] : Array(40).fill(battleMode ? ELF : 89631139),
    extra: [], side: [] };
const deckFile = process.env.MAGISTUS_SMOKE_DECK;
const supplied = deckFile ? JSON.parse(fs.readFileSync(deckFile, 'utf8')) : fixtureDeck;
const deck = new Deck(Array.isArray(supplied) ? { main: supplied, extra: [], side: [] } : supplied);
const packetTypes = new Map(['ErrorMsg','TypeChange','HsPlayerEnter','SelectHand','SelectTp','GameMsg']
    .map(type => [P['YGOProStoc' + type].identifier, type]));
let buffer = Buffer.alloc(0), joined = false, started = false, finished = false, sentStart = false;
let turn = 0, lost = 0, phase = 0, self = -1, pendingSummons = 0, summons = 0, nibiruChain = 0;
let maxxPlayed = false, draws = 0;
let hostPolicy, hostSummons = 0, hostPending = 0, tokenPosition;
let hostSummoned = false;
const board = new Map();
const evidence = [];
function send(type, fields = {}) {
    socket.write(new P['YGOProCtos' + type]().fromPartial(fields).toFullPayload());
}
function consume(packet) {
    const type = packetTypes.get(packet[2]);
    if (!type || finished) return;
    const message = new P['YGOProStoc' + type]().fromFullPayload(packet);
    if (type === 'ErrorMsg') throw new Error('Room rejected smoke host: ' + JSON.stringify(message));
    if (type === 'TypeChange' && message.isHost) send('Chat', { msg: which === 'pool' ? '/ai' : '/ai 大贤者' });
    if (type === 'HsPlayerEnter' && message.pos === 1) {
        assert.equal(message.name, name);
        joined = true;
        if (which === 'pool') {
            finished = true;
            console.log(JSON.stringify({ joined: name, command: '/ai' }));
            socket.end();
            return;
        }
        if (!sentStart) {
            sentStart = true;
            send('UpdateDeck', { deck });
            send('HsReady');
            setTimeout(() => send('HsStart'), 650);
        }
    }
    if (type === 'SelectHand') send('HandResult', { res: 1 + Math.floor(Math.random() * 3) });
    if (type === 'SelectTp') send('TpResult', { res: hostFirst ?
        P.TurnPlayerResult.FIRST : P.TurnPlayerResult.SECOND });
    if (type !== 'GameMsg') return;
    const game = message.msg, event = nameOf(game);
    if (event === 'Retry') throw new Error('Game engine rejected a response');
    if (event === 'Start') {
        started = true;
        self = game.playerNumber;
        if (self !== (hostFirst ? 0 : 1)) throw new Error('RETRY_SEAT');
        if (which === 'token') {
            const guides = require('./live-guides.json');
            hostPolicy = new MagistusPolicy(self, () => null, guides, null, () => {});
        }
    }
    if (event === 'NewTurn') turn++;
    if (event === 'NewPhase') {
        phase = game.phase;
        if (battleMode) evidence.push(['Phase', turn, phase]);
    }
    if ((event === 'Summoning' || event === 'SpSummoning') && game.controller !== self) pendingSummons++;
    if ((event === 'Summoning' || event === 'SpSummoning') && game.controller === self) hostPending++;
    if (event === 'Summoned' || event === 'SpSummoned') {
        summons += pendingSummons;
        hostSummons += hostPending;
        hostPending = 0;
        pendingSummons = 0;
    }
    if (event === 'Chaining') {
        if (game.code === 27204311 && game.controller === self) nibiruChain = game.chainCount;
        if (game.code === 23434538 && game.controller === self) maxxPlayed = true;
        evidence.push(['Chaining', game.code, game.controller, game.desc, turn]);
    }
    if (event === 'ChainNegated' || event === 'ChainDisabled') {
        if (game.chainCount === nibiruChain && nibiruChain) evidence.push(['NibiruStopped', summons]);
    }
    if (event === 'Draw' && game.player === self && turn === 1) draws += game.count;
    if (event === 'Move') {
        if (game.previous.controller !== self && game.previous.location === 4) board.delete(game.previous.sequence);
        if (game.current.controller !== self && game.current.location === 4) board.set(game.current.sequence, game.code);
        if (game.previous.controller === self && game.previous.location === 2 &&
            game.current.location === 16 && (game.reason & 0x4040) === 0x4040) lost++;
        evidence.push(['Move', game.code, game.previous.controller, game.previous.location,
            game.current.controller, game.current.location, turn]);
        if (game.code === 27204312 && game.current.controller === self && game.current.location === 4)
            tokenPosition = game.current.position;
    }
    if (which === 'token' && hostPolicy && event !== 'Win') {
        hostPolicy.observe(game);
        if (event === 'SelectIdleCmd' && tokenPosition !== undefined) {
            assert.equal(tokenPosition, 4);
            evidence.push(['TokenPosition', tokenPosition]);
            send('Surrender');
        } else if (game instanceof P.YGOProMsgResponseBase && game.responsePlayer() === self) {
            const response = event === 'SelectChain' && hostSummons >= 5 && !game.chains.some(chain => chain.forced)
                ? game.prepareResponse() : hostPolicy.choose(game);
            send('Response', { response });
        }
        return;
    }
    if (event === 'SelectChain') {
        const useTrap = which === 'nibiru' && summons === 5 && !nibiruChain ||
            which === 'maxx' && turn === 1 && !maxxPlayed;
        const chain = useTrap ? game.chains.find(chain => chain.code === trap) : undefined;
        send('Response', { response: game.prepareResponse(chain) });
    }
    if (battleMode && event === 'SelectPlace')
        send('Response', { response: game.prepareResponse(game.getSelectablePlaces().slice(0, game.count || 1)) });
    if (event === 'SelectIdleCmd' && hostFirst && turn === 1) {
        const summon = battleMode && !hostSummoned && game.summonableCards.find(c => c.code === ELF);
        if (summon) hostSummoned = true;
        const response = summon ? game.prepareResponse(P.IdleCmdType.SUMMON, summon)
            : game.prepareResponse(P.IdleCmdType.TO_EP);
        send('Response', { response });
    }
    if (event === 'SelectIdleCmd' && turn === (hostFirst ? 3 : 2)) {
        assert(started && joined && phase === 4);
        const snapshot = [...board.values()];
        evidence.push(['FirstTurnResult', { board: snapshot, lost, draws }]);
        send('Surrender');
    }
    if (event === 'Win') {
        finished = true;
        assert(evidence.some(row => row[0] === (which === 'token' ? 'TokenPosition' : 'FirstTurnResult')));
        if (which === 'nibiru') assert(evidence.some(row => row[0] === 'NibiruStopped' && row[1] === 5));
        if (which === 'maxx') assert(maxxPlayed && draws <= 2);
        if (battleMode) {
            assert(hostSummoned && evidence.some(row => row[0] === 'Phase' && row[1] === 2 && row[2] === 256));
            assert(evidence.some(row => row[0] === 'Move' && row[1] === ELF && row[2] === self &&
                row[3] === 4 && row[5] === 16));
        }
        console.log(JSON.stringify({ joined: name, ended: true, botSeat: 1 - self, evidence }));
        socket.end();
    }
}
const timer = setTimeout(() => socket.destroy(new Error('Live smoke timed out')), 22000);
socket.on('connect', () => {
    send('PlayerInfo', { name: 'MagistusSmoke' });
    send('JoinGame', { version: 0x1362, pass: flags + '#Magistus-' + crypto.randomBytes(4).toString('hex') });
});
socket.on('data', data => {
    buffer = Buffer.concat([buffer, data]);
    try {
        while (buffer.length >= 3) {
            const n = buffer.readUInt16LE(0) + 2;
            if (n < 3) throw new Error('Invalid packet');
            if (buffer.length < n) break;
            const packet = buffer.subarray(0, n);
            buffer = buffer.subarray(n);
            consume(packet);
        }
    } catch (error) {
        socket.destroy(error);
    }
});
socket.on('error', error => { console.error(error.stack); process.exitCode = 1; });
socket.on('close', () => {
    clearTimeout(timer);
    if (!finished) process.exitCode = 1;
});
