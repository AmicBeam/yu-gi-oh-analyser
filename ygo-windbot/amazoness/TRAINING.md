# 亚马逊训练计划

固定 [亚马逊牌表](../Decks/AI_Amazoness.ydk) 的 40 主卡与 15 额外。用用户给的新版 super pre 卡包复现这套牌，SHA-256 为 `c520e1aa2741166aa3a4bdb395cf4df3672350c59e601dca9a16ccbd9c61de32`。Windows 使用双子试验区的 SRVPro2 fixture，亚马逊脚本放在独立 overlay。

## 先攻试训

- 每手从引擎合法提示枚举候选。按卡组规则给展开动作先验，用高分路线的同一响应前缀继续分岔。先采无坑、灰流丽、无限泡影、增殖的 G 和尼比鲁各一档。对手压力只有一张指定手坑，怪兽及陷阱均按引擎实际发出提示后行动。
- 先比较第一回合终场分，其次按可见续航与召唤数排序。新版按[通用评分](../../skills/ygo-bot-builder/references/endboard-scoring.md)调整，鲜花 1.50、乔治有装备时魔陷康 1.00 加封墓 1.00、剑圣 1.00、救出剧及罗刹各 1.00、刻不知 0.75、锁龙蛇每张有效新牌 0.50。手坑继续参与对局，泡影等普通手坑不计做场。数值以百分之一分记录，`interactions` 各项和 `draw` 相加是终场原始分，`points` 另外扣掉对手额外抽牌 0.50／张。
- 试训用终场资源代理阻抗，检查可用区、墓地与手牌或卡组有无相应资源。罗刹与救出剧尚未在对手回合逐个结算，乔治封墓暂按装备可用性计，实际打断收益有待用对手动作验证。锁龙蛇在当次效果结算时记录留下的新牌，原有手牌消耗不额外扣分。普通效果的抽牌额度为 0.50／张，当前先攻脚本只采集刻不知和锁龙蛇的抽牌分，其他来源留给覆盖对手回合的训练。
- 每局保留真实终场、响应序列和计分分项。仅对同手牌同响应前缀下分数不同、分别可重放的行动生成成对样本。线性排序只用行动、场上怪兽数和已见阻抗，牌序只服务引擎复现。留出的五卡起手与训练分开，比较先验贪心、候选权重和同预算宽搜。
- 先跑 75 手训练、20 手留出，每手最多 160 次尝试及 2.2 秒搜索。每手原子落盘。候选 JSON 供离线对照，实盘接入待后续验收。

## 下一轮验收

1. 固定战士长、诅咒师、叫声、增援、格斗战士与王女的单动局。分别验证墓地已有调整、刻不知丢调整、绵羊苏生、水晶机巧、乔治加鲜花和锁龙蛇抽滤。记录有效新牌、已用效果、自肃、第五次召唤窗口和实际送抽。
2. 模拟对手回合，逐个兑现罗刹和救出剧的去除，重放空场先苏生、两陷阱反序和同一资源冲突。覆盖诅咒师或救出剧 C1 与魅惑之舞 C2，救出剧回收不同牌的时机、剑圣支付与前场优先于陨石。审计用实际响应窗口、目标及结算分替换首轮终场代理。
3. 建独立的后攻布局和留出组，覆盖对方不同生命值、公开攻守、后场和可见干扰。按斩杀、解场、实伤、主二依次比较，验证两种女帝王路线、战士长自肃、叫声的收益选择和遇未知干扰后的重新规划。
4. 候选和规则基线用同一 fixture、牌表、搜索预算和固定输入对拍。先攻报告有效阻抗、抽牌和实际送抽，后攻报告确证可斩局的斩杀率、解场、实伤和主二。报告结算率、超时、脚本错误和回放差异，确认改善后再评估上线。

## Windows 复跑

```powershell
Set-Location D:/codex-experiments/ygoat-amazoness-20261127
$fixture = 'D:/codex-experiments/ygoat-twins-v3-pilot-20261126/workspace/fixture'
$env:NODE_PATH = "$fixture/node_modules"
node ygo-windbot/amazoness/train.cjs --fixture $fixture --data "$fixture/ygopro" --data overlay `
  --deck ygo-windbot/Decks/AI_Amazoness.ydk --output runs/pilot-rerun `
  --cases 75 --holdout 20 --attempts 160 --case-ms 2200
```

把卡包放在 `overlay/expansions/ygopro-super-pre.ypk`。脚本指纹和参数相同会复用已完成的 `case-N.json`。`inputs.json` 冻结训练和留出牌序，`candidate.json` 保存权重，`summary.json` 保存留出比较和计数。

Windows 的旧口径结果保存在 `runs/pilot-01`。该轮 75 手搜索产生 7,844 条独立有效结算、900 条重放核对与 248 对样本，候选有 427 个权重。留出组规则先验结算 20／20，旧候选结算 10／20，未达到上线门槛。

新版 `runs/pilot-02` 用同一牌表、种子及留出组按新评分重跑，并补充终场状态、乔治装备绑定、可召唤卡组资源查询和锁龙蛇结算时的留牌记录。可用 `--focus-starter 101307007` 单独探索诅咒师起手，使用独立输出目录。

新版 `runs/pilot-03` 在同一训练与留出组改为可用的表侧剑圣即计 1.00，包括可以自解放的单独站场。原始做场分写入 `boardPoints`，扣除对手额外送抽后的训练目标写入 `points`。10/20、20/20、20/20 依次是三轮候选的留出结算率。三轮回收、对拍结果和规则发布包见 本机验收记录（本机 `artifacts/amazoness/2026-11-27/README.md`）。

对手回合固定回归可沿用诅咒师＋罗刹起手、对手只通召异次元女战士的隔离服用例。罗刹 C1 与救出剧 C2 在同一窗口时，救出剧弹走唯一对方怪兽，罗刹随后只能苏生。该局面两张陷阱合计兑现一次去除。详见 [隔离服记录](README.md)。

## 全阶段训练

[全阶段训练入口](full-train.cjs)从真实 OCGCore 提示搜索与拟合两组权重。首攻组执行到对手回合结束；后攻组在不同前后场、血量与手坑下比较斩杀、解场、实伤、主二做场。公开场况驱动决策，固定牌序只供引擎复现。额外定点验证两种女帝王路线、诅咒师 C1 接魅惑 C2、救出剧回收、罗刹控制权与剑圣自解放。标准展开放在独立定向训练中，从单诅咒师做出乔治、鲜花与剑圣，并在对手回合核对两个后场去除及乔治装备消耗的时序。

```powershell
Set-Location D:/codex-experiments/ygoat-amazoness-20261127
$fixture = 'D:/codex-experiments/ygoat-twins-v3-pilot-20261126/workspace/fixture'
$env:NODE_PATH = "$fixture/node_modules"
node phases4/ygo-windbot/amazoness/full-train.cjs --fixture $fixture --data "$fixture/ygopro" `
  --data overlay --data phases4/new-scripts --deck ygo-windbot/Decks/AI_Amazoness.ydk `
  --output runs/phases-04 --cases 54 --holdout 24 --attempts 120 --case-ms 1700
```

`phases4/new-scripts/script/c23965033.lua` 来自用户给的新卡包。散装旧脚本会覆盖 YPK，必须按此叠层顺序复跑。输入清单记录训练器、策略、牌表与实读 Lua 的 SHA-256。[本机重放验收](audit-training.cjs)重新执行每条最佳轨迹和留出轨迹，输出候选的结算数、漏斩局及相对规则退步的用例。

全阶段结果与发布制品（本机 `artifacts/amazoness/2026-11-27/full-prelaunch/README.md`）包含最新 Windows 候选、定位脚本覆盖后的完整回放、隔离服冒烟与上线判定。

## 后攻斩杀专项

`lethal-train.cjs` 从匹配当前策略指纹的已训练权重继续拟合。54 手搜索用于采样，18 手用于选择模型调整幅度，24 手留出用于最终对拍。专项样本只用同一个决策前缀下、经引擎重放确认导致是否斩杀发生变化的动作对。调整范围限于后攻主一至战斗阶段的特征，保留主二和先攻权重。对拍同时记录解场数、实伤、正常进入主二和引擎错误。

```sh
NODE_PATH=/path/to/srvpro2/node_modules node ygo-windbot/amazoness/lethal-train.cjs \
  /path/to/srvpro2 ygo-windbot/Decks/AI_Amazoness.ydk /path/to/candidate.json \
  /path/to/inputs.json /path/to/lethal-run /path/to/ygopro /path/to/card-scripts
```
