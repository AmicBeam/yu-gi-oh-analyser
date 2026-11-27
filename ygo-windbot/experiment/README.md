# 双子 WindBot CPU 训练实验

双子执行器的限时搜索可以通过 `TWINS_PLAN_BRANCH_MODEL` 加载线性分支排序模型。变量未设置、文件缺失或格式校验失败时使用规则排序。模型以当前候选的场上、手牌、墓地、额外可用卡和四项局面计数为输入，最多给原搜索潜力分增减 16。终场评分、同分比较、合法动作和素材检查、短连招以及搜索时限都走原实现。高终场分候选仍有自己的保留配额。

## 生成样本、训练和导出

准备 Python 3.10+、Node.js、Mono 和 `mcs`，并按 [实盘回归文档](../tests/README.md) 准备 SRVPro2 fixture。训练只需要 fixture 的卡库、适配脚本和 OCGCore 依赖，以及包含现有终场评分的 `WindBot.exe`。如 fixture 依赖 `DEFAULT_OCGCORE_WASM_PATH`，训练和对拍使用同一个 WASM 文件的绝对路径。

### 从规则版实盘路线分叉

第二轮使用规则版的实盘响应作为搜索起点。它依次采集无坑、陨石、锁鸟和屋敷童，用同一个牌序、随机种子及双方响应在 OCGCore 重放。每手先核对重放终场的实盘分和场上位置，再从路线中的主阶段窗口尝试其他合法动作。吃坑样本从手坑结算后开始分叉，同一响应前缀下的不同走法按可达终场分比较。

```sh
python3 ygo-windbot/experiment/train.py --guided --seed 20261202 \
  --fixture /path/to/srvpro2-fixture \
  --windbot /path/to/rule-baseline/WindBot.exe \
  --output /tmp/twins-guided-train
```

引导训练使用完整的可运行 fixture，采集钩子只加载到临时的本地 SRVPro2 进程。用 `--guides-in /tmp/twins-guided-train/guided-inputs.json` 可复用已采集的引导路线。重跑时使用相同的起手数和搜索参数。历史回归集和 [新留出起手](holdout-v2.json) 均从引导训练中排除。

第二轮产物见 [引导训练模型](twins-branch-ranker-guided.tsv)，使用同一个环境变量显式加载。

### 第一轮的随机搜索

在 ygoat 根目录运行以下命令。

```sh
python3 ygo-windbot/experiment/train.py \
  --fixture /path/to/srvpro2-fixture \
  --windbot /path/to/rule-baseline/WindBot.exe \
  --output /tmp/twins-train-repro
```

默认种子为 `20261127`，12 手起手各搜索最多 200 条路线，单手预算 10 秒。固定牌序下，OCGCore 探索不同分支，给实际结算至下回合的终场打分。训练按同一响应前缀分组，比较不同主阶段动作所到达的最高已观察分数。每条样本使用分支走到下一个主阶段动作窗口时的局面作为输入。训练用 CPU 和 Python 标准库拟合权重，`pairs.jsonl`、`training-inputs.json`、`training-summary.json` 和模型写入输出目录。机器较慢时可提高 `--reference-ms`，避免时限改变探索结果。

[已训练模型](twins-branch-ranker.tsv) 的格式为 `twins-branch-ranker-v1`。每行含卡号和四个权重。普通行依次对应场上、手牌、墓地和额外卡组，额外卡组按卡号是否可用计数。卡号 0 的四项依次是召唤数（上限 5）、计分抽牌数、双子陷阱盖放张数、是否被锁鸟限制。Python 和 C# 使用同一套输入顺序。每个 WindBot 进程在首次实际裁剪搜索候选时加载一次模型。

## 本地启用

按 [WindBot 技能的构建说明](../../skills/ygo-windbot/SKILL.md) 在独立目录构建带本仓库覆盖文件的 WindBot Release。运行目录准备 `Decks/AI_Twins.ydk`、`Dialogs/`、`bots.json` 和 `cards.cdb`。在该目录启动，SRVPro2 指向这个实验端口。

```sh
TWINS_PLAN_OPENING_TABLE=0 \
TWINS_PLAN_BRANCH_MODEL=/absolute/path/to/twins-branch-ranker.tsv \
mono WindBot.exe ServerMode=true ServerPort=15181
```

加上 `TWINS_PLAN_TRACE=1` 可查看 `branch-model loaded`、`branch-model fallback` 和实际使用模型裁剪过的搜索轮数 `branch-frontiers`。规则对照使用同一个 Release，将 `TWINS_PLAN_BRANCH_MODEL` 留空。

## 留出对拍

[固定输入](holdout.json) 复用前序开局缓存替换评测的 29 条起手、牌序种子和吃灰条件，另选仓库固定用例中的陨石、灰流丽、天救龙、锁鸟、幽鬼兔、无限泡影各一手，分别跑吃坑和无坑。相同牌序、陪练和手坑条件的历史条目共享一局实测。训练和对拍都会检查五张起手的多重集是否相交。

新留出集覆盖屋敷童、陨石和锁鸟，各有红蓝起手和无坑对照。屋敷童保留到主一桑尼发动墓地苏生时，陪练只在引擎给出合法连锁时使用，并检查桑尼的连锁编号确实被无效。桑尼没有出现该窗口时，报告记为未覆盖。要合并两批验证输入，分别传入 `--holdout ygo-windbot/experiment/holdout.json` 和 `--holdout ygo-windbot/experiment/holdout-v2.json`。

```sh
python3 ygo-windbot/experiment/compare.py \
  --fixture /path/to/srvpro2-fixture \
  --windbot /path/to/experiment/WindBot.exe \
  --model ygo-windbot/experiment/twins-branch-ranker.tsv \
  --training-inputs /tmp/twins-train-repro/training-inputs.json \
  --output /tmp/twins-model-ab
```

默认串行测试，可用 `--case` 选择单个场景，或用 `--jobs` 启动多个本地 fixture。报告分别保存实际终场分、主阶段消息最大间隔、规划峰值、手坑实际落地和同分优先级。主阶段消息间隔默认只记录，显式传入 `--max-wait-ms` 才按耗时判退。两侧手坑结果不一致或均未落地时，仍保留实际分与轨迹，并单独标记覆盖缺项。

逐条分数、优先级和串行复测见 [引导训练报告](results-guided.md) 和 [第一轮报告](results.md)。

## 随机混合手坑对拍

```sh
python3 ygo-windbot/experiment/mixed_benchmark.py \
  --fixture /path/to/srvpro2-fixture \
  --windbot /path/to/experiment/WindBot.exe \
  --v1 ygo-windbot/experiment/twins-branch-ranker.tsv \
  --v2 ygo-windbot/experiment/twins-branch-ranker-guided.tsv \
  --training-inputs /path/to/v1/training-inputs.json \
  --training-inputs /path/to/v2/training-inputs.json \
  --output /tmp/twins-mixed --jobs 4
```

默认从 0 至 5 张起手手坑各取四组，双子五张牌也随机抽取。规则版和两轮模型同用一份牌序和 OCGCore 随机种子，两版模型的训练起手都从抽样中排除。每组带手坑的起手同时跑一份陪练不主动响应的对照。以上手张数分组，另记手坑是否实际生效、对手额外抽牌、末次硬阻抗后己方召唤与盖放，以及最终场面分。抽卡压力和只完成盖剑的准备动作独立记录。

陪练从引擎提供的合法响应里按固定优先级选牌，并优先用屋敷童无效桑尼。旧 fixture 缺少灾祸的根绝者正式卡号和脚本时，测试在临时副本中从预览卡数据补齐卡号，使用仓库的召唤无效测试脚本。用 `--inputs-in /tmp/twins-mixed/inputs.json --case mix-5-01` 串行复跑指定牌序。用 `--probes-only --probe 30316053` 单独检查一张手坑。

本次随机对拍的逐手终场、续展和手坑覆盖见 [随机混合手坑报告](results-mixed.md)。

### 按固定时长训练先攻

`train_first_turn_hour.py` 先逐张给陪练配一张手坑，再抽取 0–5 张起手手坑。每八手常规样本含三手无坑。每手通过 OCGCore 探索合法行动，按同一个 WindBot 评分器计算终场，额外送抽每张扣 1 分。最佳已计分路线须复现终场、陨石窗口与送抽数才保留该局的排序样本。

```sh
PYTHONPATH=ygo-windbot/tests python3 ygo-windbot/experiment/train_first_turn_hour.py \
  --fixture /path/to/fixture --windbot /path/to/WindBot.exe \
  --scorer /path/to/TwinsEngineScore.exe --output /tmp/twins-first-turn-hour \
  --seconds 3600
```

WindBot 和评分器须来自同一轮构建。输出保留逐手分片、独立留出起手、实际发动及落地覆盖。收到中断信号后，训练器停止派发新手牌，完成正在搜索的手牌并保存候选排序器。

## 第三轮残局试跑

先用 `capture_mixed_guides.py` 在可运行的本地 SRVPro2 fixture 采集规则版及两轮模型的实盘响应。用 `--exclude` 传入以往训练和留出手牌清单。采集会写出 `guides.json`，其中包含实盘牌序、随机种子、双方响应，以及经轨迹确认的阻抗窗口。

搜索机用 `recovery_pilot.py` 重放引导，核对实际终场分、站位和对手抽牌数后，再从已结算阻抗的主阶段窗口试续接。试跑同时记录终场分与对手抽牌数，每张额外抽牌按参数 `--draw-cost` 折算到排序目标，默认 0.5 分。排序器沿用现有线性特征；同一双子起手的规则版和模型回放会进入同一训练或验证分组。

Windows 使用预编译的 `TwinsEngineScore.exe`，与对应版本的 `WindBot.exe` 同目录，且在 `x64\sqlite3.dll` 放入 WindBot 自带的 Windows 原生依赖。决斗引擎资源放独立 fixture 目录。以下命令在实验目录的 `workspace` 下运行。

```powershell
python ygo-windbot\experiment\recovery_pilot.py --guides ..\inputs\guides.anchored.json --fixture fixture --windbot scorer\WindBot.exe --scorer scorer\TwinsEngineScore.exe --output ..\runs\pilot-anchored --jobs 6 --stop-after 6
```

续跑时去掉 `--stop-after 6`，保留其他输入和预算。已完成的回放按 ID 各自原子落盘，续跑跳过这些分片。目录里还包含汇总、错误记录和离线模型。

本次 Windows 的数据产量和断点复验见 [第三轮试跑报告](results-pilot-v3.md)。

### 让当前模型生成下一轮样本

给 `capture_mixed_guides.py` 传入 `--model 当前模型.tsv`，即可只采集该模型用固定种子打出的回放。先生成并冻结另一批混合起手作为验收输入。给采集命令传入历史训练和留出集的 `--exclude`，验收脚本也会按五张起手检查和拟合数据是否重叠。

在 Windows 用 `recovery_pilot.py` 搜索本轮回放并写入新目录。用 `fit_iteration.py --previous 上轮结果目录 --fresh 本轮搜索目录 --incumbent 当前模型.tsv --output 候选目录` 合并样本并重训。两批数据必须使用同一计分环境，起手不能重叠。旧试跑的离线验证分组继续保留为验证数据。

补无坑数据时，可用 `capture_mixed_guides.py --inputs-in 已冻结的起手.json`，并将搜索结果通过 `fit_iteration.py --supplement 额外搜索目录` 合并。新样本按起手手坑条件分成无坑和带坑，再按双子五张起手分组切分。每个已采集分层都需要至少两手能产出排序对的起手，分别给训练和验证。只有一手可用时会报缺口。旧搜索分片没有分层标签时，用 `--fresh-guides` 提供原始引导文件，脚本会核对 SHA-256。

将候选模型与拟合汇总带回实盘机器，用 `iteration_gate.py` 在同牌序的留出局上对比当前模型和候选模型。默认检查平均终场分、额外给抽、单局下降 0.5 分和同分优先级。主阶段间隔只记录，显式传入 `--max-wait-ms` 可启用性能门槛。并行运行的异常局和最差降分局会在相同输入下串行复测。脚本写出门槛结果，当前模型文件由调用方显式指定。

首次自迭代的实际晋级结果见 [闭环试跑报告](results-iteration-01.md)。

补齐无坑和分层切分的实测见 [无坑样本试跑](results-clean-balance-01.md)。

### 在恢复搜索中启用候选权重

继续用 `TWINS_PLAN_BRANCH_MODEL` 指定当前排序器。额外设置 `TWINS_PLAN_RECOVERY_MODEL=/absolute/path/to/candidate.tsv` 时，WindBot 只在确认己方连锁效果被无效、对方结算时移走己方资源或锁鸟生效之后的那次重规划中采纳候选建议。无坑的展开和抽牌重规划、单纯的对手抽卡压力与被无效的手坑沿用当前策略。暂时只能确认上述三类信号，没有确认到影响的窗口也走当前排序。

每层搜索从当前策略排序中保留至少八分之七的束宽，其余最多八分之一用于候选模型提出的额外分支。候选位置仍由既有合法动作生成，沿用相同计时、评分和同分规则。设 `TWINS_PLAN_TRACE=1` 后，可从 `recovery-confirmed` 核对进入恢复的原因，从 `recovery-frontiers` 核对候选实际改过几层搜索。

用 `iteration_gate.py --recovery-only` 对拍时，两侧都加载同一个当前排序器。候选侧额外启用恢复排行，门槛会检查无坑分数与候选是否真正参与恢复搜索。

逐手结果与串行复测见 [恢复阶段对拍报告](results-recovery-overlay.md)。

### 自迭代试跑

用 `auto_recovery_loop.py` 在有 SRVPro2 fixture 的机器上启动。它自动冻结每轮新起手，把当前策略的实盘回放传到 Windows 搜索与拟合，再拉回候选做恢复模式验收。每轮取 4 手无坑加各 1 手 1–5 张手坑作为新采集输入；独立验收用 2 手无坑加各 1 手带坑。试跑只抽至少持有一个常规双子初动或检索的起手。已使用的训练与验收手牌在后续轮次都会排除。

Windows 根目录沿用第三轮试跑的 `workspace/fixture`、`workspace/scorer` 及三个历史搜索目录。传入 `--ssh-identity`、`--ssh-target`、`--windows-root` 和一个独立 `--job`，连接使用已校验的 SSH 主机密钥。运行中的每轮输入、拟合产物、验收与晋级决定都以文件落盘。中断后可用相同参数、输出目录和 Windows 作业名继续。默认两轮、单次最多 900 秒，每条回放最多 100 次搜索与 8 秒参考预算。

上一轮被拒的候选权重留在该轮目录。已核对的新回放仍可进入下一轮候选的训练集。下一轮采集使用最后通过门槛的实验恢复模型。无坑模型继续使用指定的当前权重，实盘生产入口由用户另行决定。

两轮闭环的实际样本与拒绝原因见 [自动循环试跑报告](results-auto-loop-01.md)。

长跑可用 `--max-seconds 3600`。编排端会记录开始与截止时间，Windows 的搜索及拟合进程也有同一截止时间内的限时保护。到时保留完成的轮次和搜索分片。用 `--windows-supplemental-search` 接入已核对的历史搜索，用 `--regression-inputs` 固定回归手牌及最低终场分。固定回归失败会阻止晋级，该回归也会从新起手的平均收益中单独报告。

### 后攻解场与斩杀试跑

`train_second_turn.py` 用 OCGCore 重建从先攻实盘终场抽到的可见对方场面，再抽样手坑和剩余生命值。双子实际作为后攻方走主一、战阶和主二。训练按**斩杀、主二入口时清掉的对方场面卡数、进入主二前的实伤、主二终场分**逐项比较。引擎给出的双子解场效果、攻击动作和选对象都参与搜索。非斩杀路线须实际经过主二。候选路线从同一个响应前缀分叉，最佳路线重放通过后才产出排序样本。训练输入屏蔽对手里侧卡名。

Windows 用独立目录运行，`--seconds 1800` 约束训练时间。每手搜索结果按文件原子落盘，每十手更新 `training/summary.json` 和独立的 `training/second-turn-ranker.json`。该模型供后攻离线实验使用。现有 WindBot 的先攻权重和后攻运行入口仍各自按原配置生效。
