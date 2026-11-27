'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { C, L, F, timing, choices } = require('./duel-policy.cjs');

const card=(code, sequence=0, attack=1900, attrs={})=>({
    code, sequence, attack, defense:1000, position:1, location:L.monster, attribute:1, ...attrs,
});
const spell=code=>card(code,0,0,{location:L.spell,position:8});
const enemy=card(28985331,0,1400);
const own=card(C.sword,0,1500);
const effect=(code=enemy.code, location=L.monster)=>({code,player:1,location,sequence:0});
const ready={
    own:[own], enemy:[enemy], hand:[C.curse,C.fighter], grave:[C.fighter,C.princess,C.sword],
};
const table=[
    // id, Lua string index, activation zone, decision state, visible board, expected activation
    [C.nibiru,-1,L.hand,{turnPlayer:1},{own:[],enemy:[enemy,card(7572887,1)]},true],
    [C.nibiru,-1,L.hand,{turnPlayer:1},{own:[own],enemy:[enemy,card(7572887,1)]},false],
    [C.nibiru,-1,L.hand,{turnPlayer:1,swordNegateUsed:true},{own:[own],enemy:[enemy,card(7572887,1)]},true],
    [C.nibiru,-1,L.hand,{turnPlayer:1,spentBaronne:new Set([0])},
        {own:[card(C.baronne)],enemy:[enemy,card(7572887,1)]},true],
    [C.nibiru,-1,L.hand,{turnPlayer:1},{own:[card(C.george)],enemy:[enemy,card(7572887,1)]},true],
    [C.chief,-1,L.hand,{},{},true],
    [C.chief,-1,L.monster,{},{own:[card(C.baronne,0,3000)]},false],
    [C.chief,-1,L.monster,{phase:F.main2},{own:[card(C.baronne,0,3000)]},true],
    [C.fighter,0,L.hand,{},{},true],
    [C.fighter,1,L.monster,{},{},true],
    [C.fighter,2,L.monster,{turnPlayer:1,battle:{ours:1000,theirs:2500,damage:1500}},{},true],
    [C.fighter,2,L.monster,{turnPlayer:1,battle:{ours:2500,theirs:1000,damage:0}},{},false],
    [C.harpie,1,L.hand,{lastTargeted:0},{},true],
    [C.harpie,1,L.hand,{lastTargeted:null},{},false],
    [C.harpie,2,L.monster,{},{},true],
    [C.rose,0,L.hand,{turnPlayer:1},{},true],
    [C.rose,0,L.hand,{turnPlayer:1},{hand:[C.dominus,C.fighter]},false],
    [C.rose,1,L.monster,{},{},true],
    [C.princess,0,L.monster,{},{},true],
    [C.princess,1,L.monster,{phase:F.damage,battle:{ours:2200,theirs:1300}},{},true],
    [C.princess,1,L.monster,{phase:F.damage,calledTarget:true,battle:{ours:2200,theirs:1300}},{},false],
    [C.curse,0,L.monster,{},{},true],
    [C.curse,1,L.monster,{},{},true],
    [C.curse,2,L.grave,{phase:F.damageCalc,battle:{ours:1200,theirs:2400}},{},true],
    [C.curse,2,L.grave,{phase:F.damageCalc,battle:{ours:2400,theirs:1200}},{},false],
    [C.belle,-1,L.hand,{chain:[effect()]},{},true],
    [C.belle,-1,L.hand,{},{},false],
    [C.ash,-1,L.hand,{chain:[effect(C.maxx,L.hand)]},{},true],
    [C.ash,-1,L.hand,{chain:[effect(),{code:C.sword,player:0,location:L.monster},effect(C.call,L.spell)]},{},true],
    [C.ash,-1,L.hand,{},{},false],
    [C.ogre,-1,L.hand,{chain:[effect(97045737,L.spell)]},{},true],
    [C.ogre,-1,L.hand,{chain:[effect()]},{},false],
    [C.maxx,0,L.hand,{turnPlayer:1},{},true],
    [C.maxx,0,L.hand,{turnPlayer:1,maxxUsed:true},{},false],
    [C.droll,0,L.hand,{turnPlayer:1},{},true],
    [C.droll,0,L.hand,{turnPlayer:1,maxxUsed:true},{},false],
    [C.veiler,0,L.hand,{turnPlayer:1,chain:[effect()]},{},true],
    [C.veiler,0,L.hand,{turnPlayer:1,chain:[effect()]},{enemy:[{...enemy,status:1}]},false],
    [C.rota,-1,L.spell,{},{},true],
    [C.mirror,0,L.spell,{},{},true],
    [C.mirror,1,L.grave,{},{},true],
    [C.call,0,L.spell,{},{},true],
    [C.call,1,L.grave,{},{own:[card(C.sword,0,3100)],enemy:[enemy,card(7572887,1)]},true],
    [C.call,1,L.grave,{lp:[8000,2000]},{own:[card(C.sword,0,2600),card(C.chief,1),card(C.fighter,2,1500)],
        enemy:[{...enemy,position:4},{...card(7572887,1),position:4}]},false],
    [C.call,1,L.grave,{},{},false],
    [C.formation,0,L.spell,{turnPlayer:1},{},true],
    [C.formation,0,L.spell,{turnPlayer:1},{own:[]},true],
    [C.formation,0,L.spell,{turnPlayer:1,chain:[{code:C.rescue,player:0}]},{},false],
    [C.imperm,0,L.spell,{chain:[effect()]},{},true],
    [C.imperm,0,L.hand,{chain:[effect()]},{enemy:[{...enemy,status:1}]},false],
    [C.dominus,0,L.spell,{chain:[effect()]},{},true],
    [C.dominus,0,L.hand,{chain:[effect()]},{},false],
    [C.rescue,0,L.spell,{turnPlayer:1},{},true],
    [C.rescue,0,L.spell,{turnPlayer:1,chain:[{code:C.formation,player:0}]},{},false],
    [C.rescue,1,L.grave,{turnPlayer:1},{},true],
    [C.rescue,1,L.grave,{turnPlayer:1},{grave:[C.harpie]},true],
    [C.rescue,1,L.grave,{turnPlayer:1,phase:F.standby},{grave:[C.harpie]},false],
    [C.empress,0,L.monster,{},{},true],
    [C.sword,0,L.monster,{},{},true],
    [C.sword,1,L.monster,{},{},true],
    [C.sword,2,L.monster,{turnPlayer:1,chain:[effect()]},{own:[own]},true],
    [C.sword,2,L.monster,{turnPlayer:1,chain:[effect()]},{enemy:[{...enemy,status:1}]},false],
    [C.baronne,0,L.monster,{},{},true],
    [C.baronne,0,L.monster,{},{enemy:[]},false],
    [C.baronne,1,L.monster,{chain:[effect()]},{},true],
    [C.baronne,1,L.monster,{},{},false],
    [C.baronne,2,L.monster,{spentBaronne:new Set([0])},{own:[card(C.baronne)]},true],
    [C.baronne,2,L.monster,{},{own:[card(C.baronne)]},false],
    [C.legion,0,L.monster,{},{},true],
    [C.legion,1,L.grave,{},{},true],
    [C.shrimp,0,L.monster,{},{enemy:[spell(44095762)]},true],
    [C.shrimp,0,L.monster,{},{},false],
    [C.shrimp,1,L.monster,{turnPlayer:1},{own:[card(C.shrimp),card(C.empress,1,3000,{level:8})]},true],
    [C.shrimp,1,L.monster,{turnPlayer:1},{own:[card(C.shrimp),card(C.fighter,1,1400,{level:4})]},false],
    [C.tornado,0,L.monster,{},{enemy:[spell(44095762)]},true],
    [C.tornado,0,L.monster,{},{},false],
    [C.exciton,0,L.monster,{},{enemy:[enemy,spell(44095762)],own:[card(C.exciton)]},true],
    [C.exciton,0,L.monster,{lp:[8000,1000]},{enemy:[],own:[card(C.exciton,0,1900)]},false],
    [C.dugares,0,L.monster,{},{},true],
    [C.goddess,0,L.monster,{},{},true],
    [C.goddess,1,L.monster,{chain:[effect()]},{},true],
    [C.goddess,1,L.monster,{},{},false],
    [C.saryuja,0,L.monster,{},{},true],
    [C.saryuja,1,L.monster,{},{hand:[C.ash]},false],
    [C.saryuja,1,L.monster,{},{hand:[C.curse]},true],
    [C.saryuja,2,L.monster,{},{},true],
    [C.george,0,L.monster,{},{},true],
    [C.george,1,L.monster,{chain:[effect(12580477,L.spell)]},{},true],
    [C.george,1,L.monster,{},{},false],
    [C.prism,0,L.monster,{},{},true],
    [C.prism,1,L.monster,{turnPlayer:1,phase:8},{},true],
    [C.prism,1,L.monster,{turnPlayer:1},{},false],
    [C.dragon,0,L.monster,{},{},true],
    [C.dragon,0,L.monster,{},{grave:[C.princess]},false],
    [C.sheep,0,L.monster,{},{},true],
];

table.forEach(([id,index,zone,adjust,view,expected],ordinal)=>{
    test(`${ordinal+1} · ${id}/${index} zone ${zone} · ${expected?'activate':'pass'}`,()=>{
        const state={bot:0,turn:2,turnPlayer:0,phase:F.main1,lp:[8000,8000],chains:new Map(),
            spentBaronne:new Set(), ...adjust};
        if (adjust.chain) state.chains=new Map(adjust.chain.map((link,i)=>[i,link]));
        assert.equal(timing(id,index,zone,state,{...ready,...view})>0,expected);
    });
});

test('every distinct code in the fixed deck has a policy timing test',()=>{
    const ids=new Set(fs.readFileSync(path.join(__dirname,'../Decks/AI_Amazoness.ydk'),'utf8')
        .split(/\r?\n/).filter(line=>/^\d+$/.test(line)).map(Number));
    assert.deepEqual([...ids].filter(id=>!table.some(row=>row[0]===id)),[]);
});

test('Dugares chooses lethal doubling ahead of draw when legal',()=>{
    const P=require('ygopro-msg-encode');
    const msg=new P.YGOProMsgSelectOption();
    msg.options=[C.dugares*16,C.dugares*16+2];
    const {searchPolicy}=require('./duel-policy.cjs');
    const state={bot:0,turn:2,turnPlayer:0,phase:F.main1,lp:[8000,3300],chains:new Map(),searchPolicy};
    const order=choices(msg,state,ready).sort((a,b)=>b.weight-a.weight);
    assert.equal(order[0].key,String(C.dugares*16));
    const lethal=choices(msg,state,{...ready,own:[card(C.sword,0,2200)]}).sort((a,b)=>b.weight-a.weight);
    assert.equal(lethal[0].key,String(C.dugares*16+2));
});

test('skip a disabled Swordmaster summon trigger despite a legal engine prompt',()=>{
    const P=require('ygopro-msg-encode');
    const {searchPolicy}=require('./duel-policy.cjs');
    const msg=new P.YGOProMsgSelectEffectYn();
    Object.assign(msg,{player:0,controller:0,code:C.sword,location:L.monster,sequence:0,desc:C.sword*16});
    const state={bot:0,turn:1,turnPlayer:0,phase:F.main1,lp:[8000,8000],chains:new Map(),searchPolicy};
    const offered=choices(msg,state,{...ready,own:[{...own,status:1}]});
    assert.deepEqual(offered.map(o=>o.key),[`${C.sword}:${C.sword*16}:false`]);
});

test('aim an available negate at the current enemy effect instead of the biggest monster',()=>{
    const P=require('ygopro-msg-encode');
    const {searchPolicy}=require('./duel-policy.cjs');
    const strong=card(7572887,1,7000);
    const msg=new P.YGOProMsgSelectCard();
    Object.assign(msg,{player:0,min:1,max:1, cards:[enemy,strong].map(c=>({...c,controller:1}))});
    const state={bot:0,turn:2,turnPlayer:1,phase:F.main1,lp:[8000,8000],
        chains:new Map([[1,effect()]]),pendingEffect:C.sword,hint:P.OcgcoreScriptConstants.HINTMSG_DISABLE,searchPolicy};
    const aimed=choices(msg,state,{...ready,enemy:[enemy,strong]}).sort((a,b)=>b.weight-a.weight)[0];
    assert.equal(aimed.selectedCards[0].sequence,enemy.sequence);
});

test('summon positions follow the shared rules in live and offline decisions',()=>{
    const P=require('ygopro-msg-encode');
    const { searchPolicy }=require('./duel-policy.cjs');
    const { AmazonessPolicy }=require('./live-policy.cjs');
    const cardReader=code=>({
        [C.sword]:{ attack:2600, defense:2200 },
        [C.fighter]:{ attack:1500, defense:1300 },
        [C.chief]:{ attack:1900, defense:2000 },
        [C.prism]:{ attack:1500, defense:0 },
        [C.veiler]:{ attack:0, defense:0 },
    })[code];
    const live=new AmazonessPolicy(0,cardReader,{weights:[{'a:SelectPosition:4':100}]},()=>{});
    live.guide=()=>{ throw new Error('Summon position went through the combo guide'); };
    const fixtures=[
        [C.sword,1,0,F.main1,5,1],
        [C.sword,2,0,F.main2,5,1],
        [C.fighter,1,0,F.main1,5,4],
        [C.chief,2,0,F.main2,5,4],
        [C.fighter,2,0,F.main1,5,1],
        [C.fighter,2,1,F.main1,5,1],
        [C.veiler,2,1,F.main1,5,4],
        [C.nibiru+1,2,1,F.main1,5,4],
        [C.nibiru+1,2,1,F.main1,1,1],
        [C.sword,1,0,F.main1,4,4],
    ];
    for (const [code,turn,turnPlayer,phase,positions,expected] of fixtures) {
        const msg=new P.YGOProMsgSelectPosition();
        Object.assign(msg,{player:0,code,positions});
        const state={bot:0,turn,turnPlayer,phase,cardReader,searchPolicy};
        Object.assign(live.state,state);
        assert.deepEqual(choices(msg,state,ready).map(option=>option.key),[String(expected)]);
        assert.deepEqual(live.choose(msg),msg.prepareResponse(expected));
    }
});

test('the standard guide only accepts the expected optional trigger at its current step',()=>{
    const P=require('ygopro-msg-encode');
    const { standardChooser }=require('./train.cjs');
    const choose=standardChooser();
    const state={chains:new Map()};
    const summon={key:`${P.IdleCmdType.SUMMON}/${C.curse}:0:2:0/0`,card:C.curse};
    assert.equal(choose({msg:{},state,name:'SelectIdleCmd',choices:[summon]}),summon);

    const yes={key:'prompt:true'}, no={key:'prompt:false'};
    const prompt=(code,desc,name='SelectEffectYn')=>choose({msg:{code,desc},state,name,choices:[yes,no]});
    assert.equal(prompt(C.curse,221),yes);
    assert.equal(prompt(C.curse,C.curse*16+2),undefined);
    assert.equal(prompt(C.sword,221),undefined);
    assert.equal(prompt(0,0,'SelectYesNo'),undefined);
});
