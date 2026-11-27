'use strict';

const belle = 73642296;
const sunny = 93672138;

// Hold Belle for Sunny's revival. The engine still has to offer a legal chain.
function sunnyRevival(event) {
    return event?.[0] === 'Chaining' && event[1] === sunny && event[2] === 0
        && event[4] === sunny * 16;
}

module.exports = { belle, sunnyRevival };
