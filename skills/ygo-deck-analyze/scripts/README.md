# 离线起手搜索

从 YDK 的五张起手开始，由 YGOPro 引擎生成可用动作。搜索用卡组策略排序动作，从起手和已探索路线的分支扩展，运行到对手抽卡阶段结算完成。无需对局录像。削手大贤者使用 `handrip-profile.cjs`，复用双子离线搜索的引擎响应编码。其他卡组需要提供自己的动作排序、对手策略和评分。

## 批量比较

使用包含 SRVPro2 构建产物和 Node 依赖的测试目录，另提供对应的卡库和 Lua 目录。最后的目录优先加载同名脚本。各输入 YDK 的主卡张数相同，并使用唯一文件名。

```sh
python3 skills/ygo-deck-analyze/scripts/sweep-hands.py 原牌表.ydk 候选.ydk \
  --fixture /path/to/srvpro2-build \
  --data /path/to/base/ygopro /path/to/overlay \
  --samples 18 --budget-ms 3800 --attempts 85 --workers 8 \
  --seed 20261126 --output /path/to/analysis/构筑采样.json
```

每档用相同开局位置和对方牌序比较各候选。保留同名卡所在位置，用新增卡填入删掉的牌位。对手 40 张牌，其中 29 张手坑，剩余牌用无效果部件填充。手坑种类和张数定义见脚本的 `HAND_TRAPS`。手坑按各档无放回抽样，实际可发动窗口以及属性限制由引擎决定。对手策略按削手大贤者的关键效果选择响应对象。分数先按档求平均，再用超几何分布求加权均值。

报告保存每手已结算的最高分、实际削手、额外抽牌、有效削手档、终场、妨碍和引擎事件。结算错误和未尝试成功的引擎提示也会保留。调卡结论需要同时检查起手样本与搜索覆盖，特别是高分基准和候选差距接近时。

## 固定起手

`search-hand.cjs` 从标准输入读 JSONL。每行包含 `input.main`、`input.extra`、`input.opponent.main`、`input.opponent.extra`、`input.draw_seed` 和 `input.trapCount`。两方主卡数组的前五张为起手，其余按指定顺序进入牌组。每行可指定 `id`、`budget`（毫秒）和 `attempts`。设 `input.witnesses` 可另取抽牌和挡下陨石的实跑结果。设 `input.earlySavage` 可增加优先做狞猛龙的分支。

单卡起手试验可在 `input.preserveOpening` 列出陪衬卡的卡号。消耗这些卡的路线会被剔除，避免把陪衬卡提供的素材或费用算进单卡展开。

```sh
NODE_PATH=/path/to/srvpro2-build/node_modules \
  node skills/ygo-deck-analyze/scripts/search-hand.cjs \
  /path/to/srvpro2-build /path/to/base/ygopro /path/to/overlay < hands.jsonl
```

削手档以第一回合和对手抽卡阶段的实际削手减去对手额外抽牌计分。常规回合抽牌不扣。只有前一回合已削五张且对手抽卡阶段确实又削一张，净值也达到六张，才计 15 分。狞猛龙需要有可用枪管指示物。龙王绝火需要表侧且效果可用。

本地研究用的牌表和样本保存到任务的分析目录。缺卡、缺 Lua 或 Lua 运行错误会中止该批结果。
