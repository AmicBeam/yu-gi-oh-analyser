---
name: ygo-windbot
description: 构建、调试或回归测试 SRVPro2 的双子 WindBot。适用于 TwinsExecutor、双子人机牌表、终场评分、起手规划、局面表、手坑对拍，以及合并 WindBot 的 cards.cdb。
---

# 双子 WindBot

定位仓库根目录。以 `ygo-windbot/Game/AI/Decks/` 为执行器源码，以 `ygo-windbot/Decks/` 为 YDK 和开局局面表。读取根目录 `README-zh.md` 的“双子人机”段，以及 `ygo-windbot/tests/README.md` 的对应回归场景。

## 本地构建

1. 在独立的 WindBot 构建目录中准备 SRVPro2 使用的 WindBot 分支，基线提交为 `6abb46a36a1da10fa37521ff2bb37c63aa90fcb9`。复制 `Game` 和 `Decks` 覆盖文件，应用 `ygo-windbot/loopback.patch`，按上游构建约定生成 Release。原双子使用默认短连招和限时搜索。双子2使用房间倒计时管理决策时间，做场搜索按状态数和深度收束。需要对拍开局缓存时，使用仓库保留的 `Decks/AI_Twins.plan.jsonl`。
2. 在 Release 运行目录准备 `Dialogs/`、`Decks/`、`bots.json` 和 `cards.cdb`。给已有 bot 名单追加双子项时保留其他条目。需要自定义卡时先用 `ygo-ypk` 构建扩展，再按优先级从低到高传入合并脚本。

   双子2的先攻候选排序权重放在 `Models/Twins2.first-turn-ranker.tsv`，后攻权重放在 `Models/Twins2.second-turn-ranker.json`。隔离服点名测试需核对权重加载和实际参与排序的日志。

   ```sh
   python3 ygo-windbot/merge-cards.py --base /path/to/base.cdb \
     --deck ygo-windbot/Decks/AI_Twins.ydk --output /path/to/runtime/cards.cdb \
     /path/to/expansion.ypk
   ```

## 本地验收

准备测试文档要求的 SRVPro2 fixture、WindBot Release 和依赖。按变更选择规则测试、固定开局、手坑对拍、主二交接或局面表回归。涉及终场结算时读取 `report.json` 和 `summary.json`，记录种子和所用牌表、局面表及 fixture。构建成功、脚本可运行和实际对局通过分别记录；缺少运行依赖时注明具体缺项。

如果规则测试在上述基线上请求 `Dialogs/default.json.json`，在独立测试运行目录中为 `Dialogs/default.json` 建同内容副本，再复跑规则测试。该基线的读取逻辑会为测试传入的文件名追加扩展名。
