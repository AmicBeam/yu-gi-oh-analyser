'use strict';

const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const path = require('node:path');
const { parseArgs } = require('node:util');
const P = require('ygopro-msg-encode');
const Deck = require('ygopro-deck-encode').default;
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { nameOf } = require('../tests/engine-search-protocol.cjs');
const { readDeck, MODEL } = require('./offline.cjs');
const { VERSION: SECOND_VERSION } = require('./second-turn.cjs');
const { MagistusPolicy } = require('./live-policy.cjs');

const LIVE_DECK = 'Magistus';
const controllerTypes = [
    'YGOProStocJoinGame', 'YGOProStocErrorMsg', 'YGOProStocSelectHand', 'YGOProStocSelectTp',
    'YGOProStocTimeLimit', 'YGOProStocChangeSide', 'YGOProStocDuelEnd', 'YGOProStocGameMsg',
];
const packetTypes = new Map(controllerTypes.map(type => [P[type].identifier, type]));

function log(event) {
    process.stdout.write(JSON.stringify(event) + '\n');
}

function seat(options, runtime) {
    const { host, port, name, password, version, hand } = options;
    const socket = net.connect({ host, port });
    let joined = false, finished = false, buffer = Buffer.alloc(0), policy, forcedHand = hand;
    const ready = () => {
        send('YGOProCtosUpdateDeck', { deck: runtime.deck });
        send('YGOProCtosHsReady');
    };
    function send(type, fields = {}) {
        if (!socket.destroyed) socket.write(new P[type]().fromPartial(fields).toFullPayload());
    }
    const joining = new Promise((resolve, reject) => {
        const timer = setTimeout(() => socket.destroy(new Error('Magistus join timed out')), 5000);
        socket.on('connect', () => {
            send('YGOProCtosPlayerInfo', { name });
            send('YGOProCtosJoinGame', { version, pass: password });
        });
        socket.on('error', error => {
            log({ event: 'seat-error', message: error.message });
            if (!joined) reject(error);
        });
        socket.on('close', () => {
            clearTimeout(timer);
            if (!joined) reject(new Error('Room closed before Magistus joined'));
            runtime.seats.delete(socket);
        });
        function handle(packet) {
            const type = packetTypes.get(packet[2]);
            if (!type) return;
            const msg = new P[type]().fromFullPayload(packet);
            switch (type) {
            case 'YGOProStocErrorMsg':
                throw new Error('SRVPro2 rejected the seat or deck');
            case 'YGOProStocJoinGame':
                clearTimeout(timer);
                if (!joined) {
                    joined = true;
                    ready();
                    log({ event: 'joined', deck: LIVE_DECK, name });
                    resolve();
                }
                break;
            case 'YGOProStocSelectHand':
                send('YGOProCtosHandResult', { res: forcedHand || 1 + Math.floor(Math.random() * 3) });
                forcedHand = undefined;
                break;
            case 'YGOProStocSelectTp':
                send('YGOProCtosTpResult', { res: P.TurnPlayerResult.FIRST });
                break;
            case 'YGOProStocChangeSide':
                ready();
                break;
            case 'YGOProStocTimeLimit':
                if (policy && policy.own(msg.player)) send('YGOProCtosTimeConfirm');
                break;
            case 'YGOProStocDuelEnd':
                finished = true;
                log({ event: 'room-duel-end', deck: LIVE_DECK });
                socket.end();
                break;
            case 'YGOProStocGameMsg': {
                const { msg: game } = msg;
                const gameName = nameOf(game);
                if (gameName === 'Retry') throw new Error('Rule engine rejected a live response');
                if (gameName === 'Start') {
                    if (game.observerFlag || ![0, 1].includes(game.playerNumber))
                        throw new Error('Magistus requires a two-player seat');
                    policy = new MagistusPolicy(game.playerNumber, runtime.cardReader, runtime.guides, runtime.model,
                        log, runtime.extra, runtime.secondModel);
                    log({ event: 'duel-start', seat: game.playerNumber });
                }
                if (!policy) break;
                policy.observe(game);
                if (game instanceof P.YGOProMsgResponseBase && policy.own(game.responsePlayer())) {
                    const started = performance.now();
                    const response = policy.choose(game);
                    send('YGOProCtosResponse', { response });
                    log({ event: 'response', prompt: gameName, ms: Math.round(performance.now() - started) });
                }
                if (gameName === 'Win') log({ event: 'duel-win-message', player: game.player });
                break;
            }
            }
        }
        socket.on('data', bytes => {
            buffer = Buffer.concat([buffer, bytes]);
            try {
                while (buffer.length >= 3 && !finished) {
                    const length = buffer.readUInt16LE(0) + 2;
                    if (length < 3) throw new Error('Invalid room packet');
                    if (buffer.length < length) return;
                    const packet = buffer.subarray(0, length);
                    buffer = buffer.subarray(length);
                    handle(packet);
                }
            } catch (error) {
                socket.destroy(error);
            }
        });
    });
    runtime.seats.add(socket);
    return joining;
}

function relay(url, response, upstream) {
    const target = new URL(upstream);
    for (const [name, value] of url.searchParams) target.searchParams.set(name, value);
    const client = target.protocol === 'https:' ? https : http;
    const request = client.get(target, { timeout: 6000 }, upstreamResponse => {
        response.writeHead(upstreamResponse.statusCode || 502);
        upstreamResponse.pipe(response);
    });
    request.on('timeout', () => request.destroy(new Error('WindBot upstream timed out')));
    request.on('error', () => {
        if (response.headersSent) response.destroy();
        else response.writeHead(502).end('WindBot upstream unavailable');
    });
}

async function main() {
    const { values: args } = parseArgs({ options: {
        fixture: { type: 'string' }, data: { type: 'string', multiple: true }, model: { type: 'string' },
        deck: { type: 'string' }, guides: { type: 'string' }, upstream: { type: 'string' }, 'second-model': { type: 'string' },
        port: { type: 'string' }, 'game-host': { type: 'string' }, 'game-port': { type: 'string' },
    } });
    if (!args.fixture || !args.data?.length || !args.model || !args.upstream || !args['game-port'])
        throw new Error('Require --fixture, --data, --model, --upstream, and --game-port');
    const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
    const model = read(args.model);
    if (model.version !== MODEL || !model.weights) throw new Error('Invalid Magistus model');
    const guides = read(args.guides || path.join(__dirname, 'live-guides.json'));
    const engine = await loadEngine(path.resolve(args.fixture), args.data.map(p => path.resolve(p)));
    const deck = readDeck(args.deck || path.join(__dirname, 'AI_Magistus.ydk'));
    for (const code of [...deck.main, ...deck.extra]) {
        if (!engine.cardReader(code)) throw new Error('Missing Magistus card ' + code);
    }
    const number = (input, defaultValue) => {
        const n = Number(input ?? defaultValue);
        if (!Number.isInteger(n) || n < 1 || n > 65535) throw new Error('Invalid port or protocol version');
        return n;
    };
    const port = number(args.port, 2399), gamePort = number(args['game-port']);
    const gameHost = args['game-host'] || '127.0.0.1';
    const upstream = new URL(args.upstream);
    if (!['http:', 'https:'].includes(upstream.protocol) ||
        (['127.0.0.1', 'localhost', '[::1]'].includes(upstream.hostname) && Number(upstream.port || 80) === port))
        throw new Error('The WindBot HTTP upstream must use a separate endpoint');
    const secondModel = args['second-model'] && read(args['second-model']);
    if (args['second-model'] && (secondModel?.version !== SECOND_VERSION || !secondModel.weights ||
        typeof secondModel.weights !== 'object' || Array.isArray(secondModel.weights) ||
        Object.values(secondModel.weights).some(v => !Number.isFinite(v)))) throw new Error('Invalid Magistus second-turn model');
    const runtime = { model, secondModel, guides, deck: new Deck({ ...deck, side: [] }), extra: deck.extra,
        cardReader: engine.cardReader, seats: new Set() };

    const server = http.createServer(async (request, response) => {
        try {
            if (request.method !== 'GET') return response.writeHead(405).end();
            const url = new URL(request.url, 'http://127.0.0.1');
            if (url.pathname === '/healthz') return response.writeHead(200, {
                'Magistus-Second-Turn': runtime.secondModel?.version || 'disabled',
            }).end('ok');
            const deck = url.searchParams.get('deck');
            if (deck && deck !== LIVE_DECK) return relay(url, response, upstream);
            if (deck !== LIVE_DECK) return response.writeHead(400).end('Missing deck');
            const password = url.searchParams.get('password') || '';
            if (!/^AIJOIN#[a-zA-Z0-9]{12}$/.test(password)) return response.writeHead(400).end('Missing join token');
            if (runtime.seats.size >= 16) return response.writeHead(503).end('Magistus seats busy');
            const hand = Number(url.searchParams.get('hand') || 0);
            await seat({ name: url.searchParams.get('name') || '大贤者', password,
                version: number(url.searchParams.get('version'), 0x1362), host: gameHost, port: gamePort,
                hand: [1,2,3].includes(hand) ? hand : 0 }, runtime);
            response.writeHead(200).end('ok');
        } catch {
            response.writeHead(502).end('Magistus join failed');
        }
    });
    server.listen(port, '127.0.0.1', () => log({ event: 'listening', port, deck: LIVE_DECK,
        upstream: upstream.origin, guides: guides.length, secondModel: secondModel?.version }));
    for (const signal of ['SIGINT', 'SIGTERM']) {
        process.once(signal, () => {
            for (const socket of runtime.seats) socket.destroy();
            server.close();
        });
    }
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
