'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { isMainThread } = require('node:worker_threads');
if (!isMainThread) return;
const { Room } = require(path.join(process.cwd(), 'dist/src/room/room'));
const createOcgcore = Room.prototype.createOcgcore;
Room.prototype.createOcgcore = function (record) {
    if (/(?:^|#)Twins-/.test(this.name)) {
        const seed = JSON.parse(fs.readFileSync(path.resolve('duel-seed.json'), 'utf8'));
        record.seed = seed;
    }
    return createOcgcore.call(this, record);
};
