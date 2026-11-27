'use strict';

// Optional SRVPro2 preload: route explicit Magistus joins without moving the legacy listener.
const { isMainThread } = require('node:worker_threads');
if (isMainThread) start();

function start() {
    const fs = require('node:fs');
    const path = require('node:path');
    const { spawn } = require('node:child_process');
    const configFile = process.env.MAGISTUS_LIVE_CONFIG || path.join(__dirname, 'runtime.json');
    const config = JSON.parse(fs.readFileSync(configFile, 'utf8'));
    const root = path.resolve(config.fixture);
    const { WindBotProvider } = require(path.join(root, 'dist/src/feats/windbot/windbot-provider.js'));
    const request = WindBotProvider.prototype.requestWindbotJoin;
    if (typeof request !== 'function' || typeof WindBotProvider.prototype.getBotByNameOrDeck !== 'function')
        throw new Error('Incompatible SRVPro2 WindBot provider');
    const port = config.port ?? 2401;
    if (!Number.isInteger(port) || port < 1 || port > 65535 || !config.gamePort ||
        !config.upstream || !config.model || !Array.isArray(config.data) || !config.data.length)
        throw new Error('Invalid Magistus runtime configuration');
    const endpoint = `http://127.0.0.1:${port}`;
    WindBotProvider.prototype.requestWindbotJoin = function (room, name, options) {
        if (name && this.getBotByNameOrDeck(name)?.deck === 'Magistus') {
            // Each call gets its own endpoint while sharing the provider's token maps.
            const routed = Object.create(this, { endpoint: { value: endpoint } });
            return request.call(routed, room, name, options);
        }
        return request.call(this, room, name, options);
    };
    const args = [path.join(__dirname, 'live-server.cjs'), '--fixture', root,
        ...config.data.flatMap(p => ['--data', p]), '--model', config.model,
        '--port', String(port), '--game-port', String(config.gamePort), '--upstream', config.upstream];
    if (config.deck) args.push('--deck', config.deck);
    if (config.guides) args.push('--guides', config.guides);
    if (config.secondModel) args.push('--second-model', config.secondModel);
    const nodePath = [path.join(root, 'node_modules'), process.env.NODE_PATH].filter(Boolean).join(path.delimiter);
    const child = spawn(process.execPath, args, { cwd: root, stdio: ['ignore', 'inherit', 'inherit'],
        windowsHide: true, env: { ...process.env, NODE_PATH: nodePath } });
    child.on('error', error => console.error('Magistus service could not start: ' + error.message));
    child.on('exit', (code, signal) => {
        if (code || signal) console.error('Magistus service exited: ' + (code ?? signal));
    });
    process.once('exit', () => child.kill());
}
