'use strict';

const { spawn } = require('node:child_process');
const net = require('node:net');
const path = require('node:path');
const P = require('ygopro-msg-encode');
const { nameOf } = require('./engine-search-protocol.cjs');

async function openWindBot(exe, fixture, diagnostics, botSeat = 0) {
    let data = Buffer.alloc(0), queued = [], failure, receiver;
    const sendEnvelope = packet => wire.write(packet.toFullPayload());
    const sendGame = msg => sendEnvelope(new P.YGOProStocGameMsg().fromPartial({ msg }));
    let wire;
    const server = net.createServer(socket => {
        wire = socket;
        socket.setNoDelay(true);
        socket.on('error', reject);
        socket.on('close', () => reject(new Error('WindBot closed its duel connection')));
        socket.on('data', chunk => {
            data = Buffer.concat([data, chunk]);
            while (data.length >= 3 && data.length >= data.readUInt16LE(0) + 2) {
                const length = data.readUInt16LE(0) + 2;
                const packet = data.subarray(0, length);
                data = data.subarray(length);
                if (packet[2] === P.YGOProCtosJoinGame.identifier) {
                    sendEnvelope(new P.YGOProStocTypeChange().fromPartial({ type: botSeat }));
                    const start = new P.YGOProMsgStart().fromPartial({
                        playerType: botSeat, duelRule: 5, startLp0: fixture.lp?.[0] ?? fixture.startLp, startLp1: fixture.lp?.[1] ?? fixture.startLp,
                        player0: { deckCount: fixture.decks[0].main.length, extraCount: fixture.decks[0].extra.length },
                        player1: { deckCount: fixture.decks[1].main.length, extraCount: fixture.decks[1].extra.length },
                    });
                    sendGame(start);
                    for (const card of fixture.field ?? []) {
                        const visible = card.player === botSeat || (card.position & 5) || card.location === 16;
                        const notice = new P.YGOProMsgMove().fromPartial({
                            code: visible ? card.code : 0,
                            previous: { controller: card.player, location: 0, sequence: 0, position: 0 },
                            current: { controller: card.player, location: card.location,
                                sequence: card.sequence, position: card.position },
                            reason: 0,
                        });
                        sendGame(notice);
                    }
                    ready();
                }
                if (packet[2] === P.YGOProCtosResponse.identifier) {
                    const response = packet.subarray(3);
                    if (receiver) receiver.resolve(response);
                    else queued.push(response);
                    receiver = null;
                }
            }
        });
    });
    let ready, rejectReady;
    const connected = new Promise((resolve, reject) => { ready = resolve; rejectReady = reject; });
    const reject = error => {
        failure = error;
        rejectReady(error);
        if (receiver) receiver.reject(error);
        receiver = null;
    };
    await new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', resolve);
    });
    const bot = spawn('mono', [exe, 'Deck=Twins2', 'Host=127.0.0.1', 'Port=' + server.address().port,
        'Version=4962', 'Chat=false', 'Debug=true'], { cwd: path.dirname(exe) });
    const close = () => {
        wire?.destroy();
        bot.kill();
        server.close();
    };
    for (const stream of [bot.stdout, bot.stderr]) stream.on('data', chunk => diagnostics(chunk.toString()));
    bot.once('error', reject);
    bot.once('exit', code => reject(new Error('WindBot exited with status ' + code)));
    const startup = setTimeout(() => reject(new Error('WindBot did not join the replay room')), 5000);
    try {
        await connected;
    } catch (error) {
        close();
        throw error;
    } finally {
        clearTimeout(startup);
    }
    return {
        onMessage(msg) {
            if (msg instanceof P.YGOProMsgResponseBase || nameOf(msg) === 'Start') return;
            sendGame(msg.playerView?.(botSeat) ?? msg);
        },
        async choose(msg) {
            if (failure) throw failure;
            sendGame(msg.playerView?.(botSeat) ?? msg);
            if (queued.length) return queued.shift();
            let timer;
            try {
                return await new Promise((resolve, reject) => {
                    receiver = { resolve, reject };
                    timer = setTimeout(() => reject(new Error('WindBot timed out at ' + nameOf(msg))), fixture.field ? 5000 : 3000);
                });
            } finally {
                clearTimeout(timer);
            }
        },
        close,
    };
}

module.exports = { openWindBot };
