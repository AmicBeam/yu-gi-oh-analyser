'use strict';

const fs = require('node:fs');
const net = require('node:net');
const http = require('node:http');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { parseArgs } = require('node:util');
const P = require('ygopro-msg-encode');
const Deck = require('ygopro-deck-encode').default;
const { nameOf } = require('../tests/engine-search-protocol.cjs');
const { loadEngine } = require('../../skills/ygo-deck-analyze/scripts/search-hand.cjs');
const { deckFrom } = require('./full-train.cjs');
const { AmazonessPolicy } = require('./live-policy.cjs');

const inbound=new Map(['JoinGame','ErrorMsg','SelectHand','SelectTp','TimeLimit','ChangeSide','DuelEnd','GameMsg']
    .map(name=>[P['YGOProStoc'+name].identifier,name]));
const log = obj => process.stdout.write(JSON.stringify(obj)+'\n');

function joinSeat({password, name, version, hand}, gamePort, runtime) {
    const socket=net.connect({host:'127.0.0.1',port:gamePort});
    runtime.seats.add(socket);
    let policy, accepted=false, buffer=Buffer.alloc(0), selection=hand;
    const send=(type,fields={})=>{
        if (!socket.destroyed) socket.write(new P['YGOProCtos'+type]().fromPartial(fields).toFullPayload());
    };
    const ready=()=>{send('UpdateDeck',{deck:runtime.deck});send('HsReady');};
    return new Promise((resolve,reject)=>{
        const deadline=setTimeout(()=>socket.destroy(new Error('Join timeout')),5000);
        socket.on('connect',()=>{send('PlayerInfo',{name});send('JoinGame',{version,pass:password});});
        socket.on('error',error=>{log({event:'seat-error',message:error.message});if(!accepted)reject(error);});
        socket.on('close',()=>{
            clearTimeout(deadline);
            runtime.seats.delete(socket);
            if (!accepted) reject(new Error('Closed before joining'));
        });
        const handle=packet=>{
            const type=inbound.get(packet[2]);
            if (!type) return;
            const msg=new P['YGOProStoc'+type]().fromFullPayload(packet);
            switch(type){
            case 'JoinGame':
                if (!accepted) { accepted=true;clearTimeout(deadline);ready();resolve(); }
                break;
            case 'ErrorMsg': throw new Error('Rejected by SRVPro2');
            case 'SelectHand':
                send('HandResult',{res:selection||1+Math.floor(Math.random()*3)});selection=0;break;
            case 'SelectTp': send('TpResult',{res:P.TurnPlayerResult.FIRST});break;
            case 'ChangeSide': ready();break;
            case 'TimeLimit':
                if (policy?.own(msg.player)) send('TimeConfirm');
                break;
            case 'DuelEnd': socket.end();break;
            case 'GameMsg': {
                const game=msg.msg, prompt=nameOf(game);
                if (prompt==='Retry') throw new Error('Engine rejected Amazoness response');
                if (prompt==='Start') {
                    if (game.observerFlag || ![0,1].includes(game.playerNumber)) throw new Error('Two-player seat required');
                    policy=new AmazonessPolicy(game.playerNumber,runtime.cardReader,runtime.model,log);
                }
                if (!policy) break;
                policy.observe(game);
                if (game instanceof P.YGOProMsgResponseBase && policy.own(game.responsePlayer())) {
                    const start=performance.now();
                    const response=policy.choose(game);
                    send('Response',{response});
                    log({event:'response',prompt,ms:Math.round(performance.now()-start)});
                }
                if (prompt==='Win') log({event:'win',player:game.player});
                break;
            }
            }
        };
        socket.on('data',bytes=>{
            buffer=Buffer.concat([buffer,bytes]);
            try {
                while (buffer.length>=3) {
                    const length=buffer.readUInt16LE(0)+2;
                    if (length<3) throw new Error('Malformed room packet');
                    if (buffer.length<length) return;
                    const packet=buffer.subarray(0,length);
                    buffer=buffer.subarray(length);
                    handle(packet);
                }
            } catch(error) { socket.destroy(error); }
        });
    });
}

async function main() {
    const {values}=parseArgs({options:{fixture:{type:'string'},data:{type:'string',multiple:true},
        deck:{type:'string'},model:{type:'string'},port:{type:'string'},'game-port':{type:'string'}}});
    const port=Number(values.port),gamePort=Number(values['game-port']);
    if (!values.fixture||!values.data||![port,gamePort].every(n=>Number.isInteger(n)&&n>0&&n<=65535))
        throw new Error('Require --fixture, --data, --port, and --game-port');
    const deckPath=values.deck||path.join(__dirname,'../Decks/AI_Amazoness.ydk');
    const deck=deckFrom(deckPath);
    const model=values.model&&JSON.parse(fs.readFileSync(values.model,'utf8'));
    if (model && (model.objective!=='amazoness-complete-duel-v1'||model.version!==1||
        model.deckSha256!==createHash('sha256').update(fs.readFileSync(deckPath)).digest('hex')||
        model.policySha256!==createHash('sha256').update(fs.readFileSync(require.resolve('./duel-policy.cjs'))).digest('hex')||
        [0,1].some(seat=>!model.weights?.[seat]||Object.values(model.weights[seat]).some(n=>!Number.isFinite(n)))))
        throw new Error('Incompatible Amazoness model');
    const {cardReader}=await loadEngine(path.resolve(values.fixture),values.data.map(p=>path.resolve(p)));
    if ([...deck.main,...deck.extra].some(code=>!cardReader(code))) throw new Error('Missing deck card data');
    const runtime={deck:new Deck({...deck,side:[]}),model,cardReader,seats:new Set()};
    const server=http.createServer(async(request,response)=>{
        const url=new URL(request.url,'http://127.0.0.1');
        if (request.method!=='GET') return response.writeHead(405).end();
        if (url.pathname==='/healthz') return response.writeHead(200,{'Amazoness-Policy':model?'model':'rules'}).end('ok');
        if (url.searchParams.get('deck')!=='Amazoness') return response.writeHead(404).end('Unknown deck');
        const password=url.searchParams.get('password')||'';
        const version=Number(url.searchParams.get('version')||0x1362);
        const hand=Number(url.searchParams.get('hand'));
        if (!/^AIJOIN#[a-zA-Z0-9]{12}$/.test(password)||!Number.isInteger(version)||version<1||version>65535)
            return response.writeHead(400).end('Invalid room request');
        if (runtime.seats.size>=16) return response.writeHead(503).end('Seats busy');
        try {
            await joinSeat({password,name:url.searchParams.get('name')||'亚马逊',version,hand:[1,2,3].includes(hand)?hand:0},
                gamePort,runtime);
            response.writeHead(200).end('ok');
        } catch {
            response.writeHead(502).end('Join failed');
        }
    });
    server.listen(port,'127.0.0.1',()=>log({event:'listening',port,model:Boolean(model)}));
    for (const signal of ['SIGINT','SIGTERM']) process.once(signal,()=>{
        for (const socket of runtime.seats) socket.destroy();
        server.close();
    });
}
main().catch(error=>{console.error(error.stack);process.exitCode=1;});
