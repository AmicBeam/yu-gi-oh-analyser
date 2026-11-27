'use strict';

// Preload only in the temporary local training server.
const fs = require('node:fs');
const path = require('node:path');
const { isMainThread } = require('node:worker_threads');
if (!isMainThread) return;

const root = path.join(process.cwd(), 'dist', 'src');
const { Room } = require(path.join(root, 'room', 'room'));
const { calculateDuelOptions } = require(path.join(root, 'utility', 'calculate-duel-options'));

const surrender = Room.prototype.onSurrender;
Room.prototype.onSurrender = async function (client, msg) {
    const record = this.lastDuelRecord;
    if (record && /(?:^|#)Twins-/.test(this.name)) {
        const capture = {
            seed: Array.from(record.seed),
            options: calculateDuelOptions(this.hostinfo),
            responses: record.responses.map(response => Buffer.from(response).toString('base64')),
        };
        fs.writeFileSync(path.resolve('training-replay.json'), JSON.stringify(capture));
    }
    return surrender.call(this, client, msg);
};
