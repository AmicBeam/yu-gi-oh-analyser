'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { scenes } = require('./train-unified-turn.cjs');

test('all three unified-ranker trials share scene coverage and split groups before fitting', () => {
    const names=['Twins2','Magistus','Amazoness'];
    const cases=names.map(name=>scenes(name,24,2026112727));
    const skeleton=rows=>rows.map(({split,kind,variant,turn,bot,lp,field})=>
        [split,kind,variant,turn,bot,lp,field.filter(c=>c.player!==bot)]);
    assert.deepEqual(skeleton(cases[0]),skeleton(cases[1]));
    assert.deepEqual(skeleton(cases[1]),skeleton(cases[2]));
    for (const [i,rows] of cases.entries()) {
        assert.equal(rows.length,48);
        assert.equal(new Set(rows.map(c=>c.key)).size,rows.length);
        assert.deepEqual(rows,scenes(names[i],24,2026112727));
        assert.equal(new Set(rows.filter(row=>row.split==='holdout').map(row=>row.kind)).size,6);
        assert(rows.filter(row=>row.turn===3).every(row=>row.field.some(c=>c.player===row.bot&&c.location===16)));
    }
});

test('Amazoness keeps an engine-offered direct lethal ahead of other model options', () => {
    const { AmazonessPolicy } = require('../amazoness/live-policy.cjs');
    const policy=new AmazonessPolicy(1,()=>null,{version:2,weights:{}},()=>{});
    policy.state.lp=[4100,8000];
    const other={key:'main2'}, first={key:'attack:1500:0:1'}, second={key:'attack:2600:2:1'};
    const attackableCards=[{sequence:0,directAttack:1},{sequence:2,directAttack:1}];
    const own=[{location:4,sequence:0,attack:1500},{location:4,sequence:2,attack:2600}];
    assert.deepEqual(policy.preserveLethal({attackableCards},'SelectBattleCmd',[other,first,second],{own,enemy:[]}),[second]);
    policy.state.lp[0]=4200;
    assert.deepEqual(policy.preserveLethal({attackableCards},'SelectBattleCmd',[other,first,second],{own,enemy:[]}),[other,first,second]);
});

test('Twins terminal-only updates leave the incumbent search and action weights intact', () => {
    const { fit } = require('./train-unified-turn.cjs');
    const model={version:2,weights:{action:0.3},frontier:{0:[0,0,0,0],36326160:[0,0,0,0]}};
    const update=fit(model,[{featureDelta:{'frontier/36326160/1':1}}],0.5,true);
    assert.deepEqual(update.weights,model.weights);
    assert.deepEqual(update.frontier,model.frontier);
    assert(update.tieFrontier[36326160][1]>0);
});

test('Twins opening focus changes training only and never reuses excluded opening hands', () => {
    const previous=scenes('Twins2',12,1928).map(row=>row.key);
    const focused=scenes('Twins2',24,2029,previous,true);
    assert(focused.every(row=>!previous.includes(row.key)));
    const train=focused.filter(row=>row.split==='train'), held=focused.filter(row=>row.split==='holdout');
    assert(train.filter(row=>row.turn===1).length>held.filter(row=>row.turn===1).length);
    assert.equal(new Set(held.map(row=>row.kind)).size,6);
});
