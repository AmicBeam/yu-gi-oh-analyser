'use strict';

const { isMainThread } = require('node:worker_threads');
if (isMainThread) configure();

function configure() {
    const fs=require('node:fs');
    const path=require('node:path');
    const {spawn}=require('node:child_process');
    const file=process.env.AMAZONESS_LIVE_CONFIG || path.join(__dirname,'runtime.json');
    const cfg=JSON.parse(fs.readFileSync(file,'utf8'));
    if (![cfg.port,cfg.gamePort].every(n=>Number.isInteger(n)&&n>0&&n<=65535) ||
        cfg.port===cfg.gamePort || !cfg.fixture || !Array.isArray(cfg.data) || !cfg.data.length)
        throw new Error('Invalid Amazoness SRVPro2 configuration');
    const fixture=path.resolve(cfg.fixture);
    const {WindBotProvider}=require(path.join(fixture,'dist/src/feats/windbot/windbot-provider.js'));
    const join=WindBotProvider.prototype.requestWindbotJoin;
    if (typeof join!=='function'||typeof WindBotProvider.prototype.getBotByNameOrDeck!=='function')
        throw new Error('Incompatible SRVPro2 WindBot provider');
    const endpoint='http://127.0.0.1:'+cfg.port;
    WindBotProvider.prototype.requestWindbotJoin=function(room,name,options) {
        if (name && this.getBotByNameOrDeck(name)?.deck==='Amazoness') {
            const routed=Object.create(this,{endpoint:{value:endpoint}});
            return join.call(routed,room,name,options);
        }
        return join.call(this,room,name,options);
    };
    const argv=[path.join(__dirname,'live-server.cjs'),'--fixture',fixture,
        ...cfg.data.flatMap(dir=>['--data',dir]),'--game-port',String(cfg.gamePort),'--port',String(cfg.port)];
    if (cfg.deck) argv.push('--deck',cfg.deck);
    if (cfg.model) argv.push('--model',cfg.model);
    const nodePath=[path.join(fixture,'node_modules'),process.env.NODE_PATH].filter(Boolean).join(path.delimiter);
    const sidecar=spawn(process.execPath,argv,{cwd:fixture,stdio:['ignore','inherit','inherit'],
        windowsHide:true,env:{...process.env,NODE_PATH:nodePath}});
    sidecar.on('error',error=>console.error('Amazoness service could not start: '+error.message));
    sidecar.on('exit',(code,signal)=>{if(code||signal)console.error('Amazoness service exited: '+(code??signal));});
    process.once('exit',()=>sidecar.kill());
}
