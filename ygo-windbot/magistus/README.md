# 削手大贤者离线人机

`offline.cjs` 是独立的 OCGCore 实验入口。它接收双方牌序，通过真实规则引擎选择、执行合法动作，从第一回合运行到对手抽卡阶段结束。可用规则策略或显式传入训练权重。当前入口覆盖先攻展开、吃坑与低停。

## 种子和训练

[牌表](AI_Magistus.ydk) 为 40 主卡和 15 额外的光泪玻璃试用版。两张决斗者之力已经换成第三张光之泪与第二张玻璃。[种子](verified-seeds.json) 有 24 条已核验响应轨迹、12 种不同五张起手。来源是无坑展开、龙王挡陨、斯彭吃灰后转线、狞猛龙保护少女检索和抽卡手坑下的低停。开始拟合前逐条在本机规则引擎复跑，并对照终场、实际削手、双方抽牌与预定手坑窗口。

每轮按对方开出 0–5 张手坑分层生成新牌序，按五张起手的多重集排除种子、留出集和历史轮次。对手的 29 张手坑配比沿用削手大贤者研究。离线搜索沿用已有卡组策略，从相同起手扩展合法分支，再以终场和送抽排序。每轮只把实际结算出正分终场的续接纳入训练。取动作、提示、已见阻抗及己方场上、手牌、墓地等特征拟合线性排序器。种子响应仅用于核验与初始拟合，随机新局直接由规则引擎探索。

首次试跑固定 12 手留出起手，每档 2 手。候选和当前策略用相同的尝试数、时间限额和随机种子对照。当前训练目标为大贤者终场分减每张额外送抽 0.5 分，与双子默认的送抽折算一致。终场里原有的净削档仍然先扣掉实际额外送抽。晋级要求全部留出的折算总分提高、对手起手有抽卡手坑的子集折算分不降、没有新增未结算样本。送抽张数仍单列记录。候选拒绝后，下轮继续用当前策略验收，合规的新搜索样本仍可加入拟合。留出的手牌和已完成轮次按文件保存。

## Windows 运行

用有 `dist/src/ygopro/card-load-worker.js` 和 `node_modules` 的 SRVPro2 fixture。另提供本轮实际核验过的 CDB、Lua 及预览卡 overlay。Windows 试跑的独立根目录为 `D:\codex-experiments\ygoat-magistus-20261127`。双子任务的 fixture 只用于加载 Node 依赖和引擎适配。

```powershell
$root = 'D:\codex-experiments\ygoat-magistus-20261127'
$fixture = 'D:\codex-experiments\ygoat-twins-v3-pilot-20261126\workspace\fixture'
Set-Location "$root\workspace"
$env:NODE_PATH = "$fixture\node_modules"
$common = @('--fixture', $fixture, '--data', 'resources/base', '--data', 'resources/overlay',
  '--deck', 'ygo-windbot/magistus/AI_Magistus.ydk')
node ygo-windbot/magistus/offline.cjs train @common --seeds ygo-windbot/magistus/verified-seeds.json `
  --output "$root\runs\pilot-01" --rounds 2 --per-stratum 5 --attempts 36 --budget 3300
```

使用相同目录、牌表、种子和参数重启会跳过完成的轮次。每轮有 `inputs.json`、分档 `observations.json`、可继续用于拟合的 `episodes.json`、`candidate.json` 及 `summary.json`。修改牌表、种子或搜索参数时用新的输出目录。根目录的 `seed-model.json` 是仅用核对路线所得的初始权重，`model.json` 在候选通过留出门槛后写出。

单局入口用 `play @common --input INPUT.json --model MODEL.json`。输入可取 `holdout.json` 中一条的 `input`，其中 `main`、`extra`、`opponent.main`、`opponent.extra` 与 `draw_seed` 指定实际牌序。省略 `--model` 可跑规则策略。

## Windows 训练记录

四轮共搜索 120 手新起手，覆盖对方起手手坑数 0–5 的全部六档。72 手的最佳搜索路线里实际发动了至少两张手坑，61 条正分续接入训练。样本、留出和种子的己方起手按五张牌多重集核对分离，四轮均用同一份光泪玻璃牌表。

| 留出总分（12 手） | 总分 | 对手额外抽牌 | 结果 |
|---|---:|---:|---|
| 规则策略 | 2.9 | 2 | 比较基线 |
| 第一轮候选 | 9.3 | 3 | 多给 1 抽，保留为实验候选 |
| 第二轮候选 | 8.5 | 9 | 多给 7 抽，保留为实验候选 |
| 第三轮候选 | 10.0 | 4 | 多给 2 抽，保留为实验候选 |
| 第四轮候选 | 8.5 | 2 | 通过门槛，成为离线实验的当前模型 |

Windows 已用第四轮晋级的 `model.json` 启动独立单局并正常结算。首两轮的检查点已用于续跑第三、第四轮。四轮用同一份 12 手留出集做迭代门槛，额外训练扣分为从第二张送抽起每张 0.3 分。 [详细验收](pilot-01.json)

## 定时扩样续训

用 `--from-run` 导入上一阶段已核验的续接和最新晋级模型，在独立的输出目录扩样。启动时核对光泪玻璃牌表和种子。旧训练起手、旧留出起手、新留出起手和最终审计起手都从新训练样本里排除。

```powershell
node ygo-windbot/magistus/offline.cjs train @common --seeds ygo-windbot/magistus/verified-seeds.json `
  --from-run "$root\runs\pilot-01" --output "$root\runs\expanded-next" `
  --seconds 2700 --rounds 100 --per-stratum 12 --gate-per-stratum 4 --attempts 72 --budget 6000
```

每轮按 0–5 张手坑各取 12 手。除当前模型引导的分支外，搜索也定期从规则策略探索。2700 秒内完成的比较按折算分门槛决定晋级。限时到时保留当前轮的 `partial.json`，同参数重启可复用已完成的搜索。最后额外跑未参与轮间晋级的 24 手审计，比较扩样结束时的模型和第 4 轮来源模型。模型权重与审计结果写入扩样目录。

切换计分时用新输出目录。可用 `--exclude-run` 排除旧口径扩样用过的起手，并以该阶段最后保存的当前权重接续。旧口径扩样的新增路径继续留档。

搜索核对对手抽卡阶段实际发生的神灵的来临换出结果，换出的可用龙王绝火计 0.9 分。有效圣月与后场己方装备锁鸟可在对手主阶段调度锁鸟，计 1 分。留场小夜的暂时除外计 0.6 分。 [定点实跑记录](scoring-witnesses.json)

### 45 分钟扩样结果

固定光泪玻璃牌表。旧口径先运行 14 分 05 秒，已落盘 258 手。剩余 30 分 55 秒按统一口径执行，旧扩样阶段的起手从新阶段的采样和验收中排除，旧阶段搜索结果留档。新版共搜索 613 手，331 手实际发动至少两张手坑，留下 378 条正分续接，其中 29 条来自结束时的未完成轮次。搜索记录中有 1 手抽卡阶段变身、9 手圣月调度锁鸟、77 手留场小夜。

新版在轮间门槛里晋级两次。实验模型在 12 轮结束时持有第 6 轮晋级权重，与第 4 轮原模型在额外冻结的 24 手独立审计上比较。

| 独立审计 | 终场总分 | 额外送抽 | 折算分 |
|---|---:|---:|---:|
| 第 4 轮原模型 | 18.5 | 12 | 12.5 |
| 扩样实验模型 | 23.5 | 18 | 14.5 |

原送抽张数门槛拒绝了这份实验权重。改用折算分门槛后，对同一批审计结果重判。对手起手有增殖的 G、山雀或水母的 12 手，折算总分从 **−4.0 升至 −3.8**，全体 24 手从 **12.5 升至 14.5**，无新增未结算样本。扩样权重由此成为当前离线实验模型。Windows 权重与重判记录在 `runs\expanded-45m-unified\model.json` 和同目录的 `promotion-adjusted.json`。 [原审计](expanded-45m.json) · [折算分重判](promotion-adjusted.json)

### 停牌分支复测

每手额外用规则引擎实跑一次直接结束回合，将实际结算结果放入候选。折算负分的展开输给折算 0 分的停牌。折算 0 分时，能留怪兽或魔陷的路线优先于空场停牌，其后再按少送抽排序。验收用例中，狞猛龙值 1 分，送两抽扣 1 分，仍然选择做出狞猛龙。

Windows 用原 24 手和原两份权重复测。抽牌手坑组 12 手逐手均为非负，规则对照折算总分 **0.0**，扩样模型 **1.2**。扩样模型 6 手选立即停牌，2 手选折算 0 分的留场路线，4 手为正分。全样本折算总分 **16.5 对 19.5**，双方各送 4 抽，模型继续通过门槛。 [实跑复测](stop-fallback-build-zero-audit.json)

## 半小时后攻训练

固定光泪玻璃牌表。对方场面来自双子训练取样的 [69 个终场布局](second-turn-opponents.json)、大贤者镜像和 [26 个通用布局](second-turn-generic-opponents.json)。通用布局覆盖高攻、高守、里侧怪兽、手坑、限制特召、无效和战斗陷阱等组合。对方剩余生命值覆盖 8000 与低血量斩杀窗口，手坑从既定 40 卡压力牌序中实际抽五张。卡片脚本负责实际结算。排序用公开的前后场数量、表示和攻守区间，也用当前合法动作和已发生的干扰。预置场面省略了此前的展开历史，例如未给预置的狞猛龙补枪管指示物。

搜索按斩杀、主二前清掉的对方场面卡数、实际伤害、主二终场折算分逐项比较。它在引擎提供的动作里探索大贤者自己的解场效果、对象和战斗顺序，并在手坑结算后的局面继续选择。最佳分支从真实引擎重跑通过后才进入排序样本。调参和最终审计各用一组未入训的六张起手。分别对比未加载后攻模块的规则版、后攻规则先验和训练权重。

```powershell
node ygo-windbot/magistus/second-turn-train.cjs --fixture $fixture `
  --data "$fixture/ygopro" --data resources/base --data resources/overlay `
  --model "$root/runs/expanded-45m-unified/model.json" `
  --boards ygo-windbot/magistus/second-turn-opponents.json `
  --generic-boards ygo-windbot/magistus/second-turn-generic-opponents.json `
  --output "$root/runs/second-turn-generic/training" `
  --seconds 1800 --jobs 5 --attempts 100 --case-ms 7000 --max-cases 20000 --holdout 50
```

通过留出审计的后攻权重可用 `live-server.cjs --second-model FILE` 或 SRVPro2 sidecar 的 `secondModel` 配置加载。其排序只用于第二回合第一次行动的坐席，阻抗和盖牌规则先于权重，先攻继续用已有展开路线和权重。`/healthz` 的 `Magistus-Second-Turn` 响应头可核验后攻模型是否加载。

### 用规则续接生成训练样本

`--repair` 会先跑通一条规则路线，再在实际出现的后攻主一提示中替换一个合法动作。两条分支此后使用同一套实战规则续接，比较实际结算结果。动作替换前的响应序列必须一致。每局最多取 12 对样本，最佳分支复跑通过后才使用。候选权重只调整主一行动排序，特征还包括己方手牌和留场资源。战斗、阻抗与主二仍由规则决定。

`--audit-generic-boards` 可指定未入训的通用布局。最终验收使用独立的六张起手，并要求相对后攻规则版逐局净改善。原本能斩杀的局仍须斩杀，原本能进主二的局须斩杀或进入主二，通用场面的结算率和逐局比较守住规则版下限。

`--combat-plan` 根据公开攻守和当前合法攻击规划攻击顺序，先比较斩杀，再比较解场和伤害。有里侧怪兽或攻守信息不全时沿用战斗规则。实际选目标和攻击仍由引擎校验。若战场变化，下一次攻击重新规划。`--continuation-base` 可从上轮权重的实际决策继续生成样本，并要求留出结果优于上轮。

## SRVPro2 房间接入

`live-server.cjs` 接收 SRVPro2 的 WindBot HTTP 入房请求，通过本机 TCP 加入指定房间。对局侧读取服务器已经发给该坐席的状态和合法动作提示。已核验路线按卡号、动作、站位匹配，找不到匹配时用训练权重和规则先验对当前候选排序。

在 SRVPro2 现有名单的 `windbots` 里加入 `{"name":"大贤者","deck":"Magistus","hidden":true}`，保留原有条目。房主在有空座的普通房间可用 `/ai 大贤者`。隐藏项仍接受点名，裸 `/ai` 的随机池沿用原名单。服务器启动时重读名单。

### 独立入口

将本目录、共享的 `ygo-windbot/tests/engine-search-protocol.cjs` 和 `skills/ygo-deck-analyze/scripts/` 下的 `search-hand.cjs`、`handrip-profile.cjs` 保持相对目录，提供审核过的 `model.json`，使用服务器的 `node_modules` 和卡库：

```sh
NODE_PATH=/path/to/srvpro2/node_modules node ygo-windbot/magistus/live-server.cjs \
  --fixture /path/to/srvpro2 --data /path/to/srvpro2/ygopro --model /path/to/model.json \
  --game-port 7911 --port 2401 --upstream http://127.0.0.1:2399
```

HTTP 入口只监听回环地址。以它为 `WINDBOT_ENDPOINT` 时，已有 WindBot 可在 2399 独立运行，其他牌组请求会原样转给它。此时将 SRVPro2 的 `windbotSpawn` 设为 0。

如果已有服务由 SRVPro2 自动拉起 WindBot，保留原 `WINDBOT_ENDPOINT` 与 `windbotSpawn`，在 Node 的 `dist/index.js` 前加 `-r /path/to/ygo-windbot/magistus/srvpro-hook.cjs`。在 preload 旁放 `runtime.json`（也可用 `MAGISTUS_LIVE_CONFIG` 指向它）：

```json
{
  "fixture": "C:/srvpro2-trial",
  "data": ["C:/srvpro2-trial/ygopro"],
  "model": "C:/srvpro2-trial/magistus-live-20261127-01/model.json",
  "gamePort": 7911,
  "port": 2401,
  "upstream": "http://127.0.0.1:2399"
}
```

preload 仅改指向大贤者的点名请求。侧车随进程启动，`GET http://127.0.0.1:2401/healthz` 可验收其监听。变更前备份启动脚本和 bot 名单，等现有连接退出后再重启。

### 实盘验收

同版 SRVPro2 隔离房间里，裸 `/ai` 召唤双子，点名大贤者可正常入房。固定单斯彭先攻、吃第 5 次召唤尼比鲁、后攻座位三组都做出双无效、四花并削 3。单斯彭面对提前发动的增殖的 G，只送 2 抽并停狞猛龙。无匹配种子的起手用权重排序正常结算。验证覆盖单局坐席和基本对局流程。 [实跑记录](live-smoke-results.json)

召唤表示沿用双子判定。己方先攻主一和己方主二，在允许表侧守备时，攻击力低于 2000 或不高于守备力的怪兽选表侧守备。0 攻击力默认守备。陨石衍生物在允许守备时始终选表侧守备。合法提示只有一种表示时照引擎要求执行。实盘回归验证了玻璃、克罗佐和龙王先攻守备、狞猛龙先攻攻击，以及对手回合发动陨石后的衍生物确实守备落地。

### 实战阻抗和收尾盖牌

已识别的阻抗按 [规则](live-rules.cjs) 从引擎给出的合法响应中选。对手发动新的效果后重新判断，同一个效果上的己方连锁处理后等结算。其他展开动作继续用已核验路线和模型。

| 时机 | 规则 |
|---|---|
| 对手抽卡阶段 | 后场神灵的来临能把场上或装备区的融合怪兽换成龙王绝火、且当前没有可用龙王时发动。优先送回暗之魔导士，再考虑装备区的融合怪兽。 |
| 对手展开前 | 有合法窗口时先交增殖的 G，再考虑山雀；已有己方抽卡效果在连锁或已经生效时暂缓重复投抽。在对手检索后，己方抽卡效果未生效也未在连锁时可交锁鸟。 |
| 对手主要阶段 | 圣月能回收己方装备的锁鸟时先回收；手中的锁鸟仍按实际检索窗口判断。陨石从引擎给出的第一个合法窗口起判断；有重要留场阻抗时要求对方至少有两只表侧怪兽。 |
| 对手发动效果 | 灰流丽优先挡增殖的 G。按合法范围用大义贼、已盖泡影、龙王、遮蒙、手中泡影及灰流丽处理；泡影和遮蒙瞄正在发动且仍在场上的怪兽。龙王优先送低代价装备，允许挡陨石；需要无效魔陷或保留龙王时由狞猛龙兜底。 |
| 光之泪与小夜 | 有空怪兽区时，光之泪可以连锁对手效果选特召。小夜优先保护被指定的己方怪兽，或在对方已有多只表侧怪兽时拆素材。暂时除外本身通常不会无效已发动的效果。 |
| 绝火苏生玻璃 | 绝火的效果从手牌或墓地特殊召唤玻璃时，跳过这张玻璃被无效的检索。通过其他方式正常特召的玻璃仍可检索。 |
| 确定结束自己的回合时 | 依次检查泡影、神灵的来临、光之泪，每个名字最多盖一张。空场时留泡影在手；能换出龙王才盖神灵；光之泪留给对方发动效果时特召。盖完继续结束回合，送抽低停也执行这个收尾。 |

隔离服实盘覆盖龙王防陨、后攻泡影和陨石、先攻回合结束盖泡影，以及吃 G 停狞猛龙后仍然盖泡影。抽卡阶段变身、圣月回收锁鸟、光之泪选特召和小夜保怪另有 [规则用例](live-rules.test.cjs)。

### 房间入口

可在隔离测试服用 `live-smoke.cjs 7911 named` 测点名，或用 `live-smoke.cjs 7911 pool` 检查原随机池。缺省的 `NC,NS` 房间标记只用于固定起手验收。

线上 7911 已通过公网点名实跑。用 `NF#233` 或其他普通房间号开房后输入 `/ai 大贤者`。线上名单确认为隐藏，公网验收只测点名。现有双子仍由原 WindBot 处理。Windows 的启动脚本和 bot 名单回滚副本位于 `C:\srvpro2-trial\magistus-live-20261127-01\before-activation`。
