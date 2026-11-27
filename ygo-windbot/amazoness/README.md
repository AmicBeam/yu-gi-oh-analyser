# 亚马逊 WindBot

当前线上入口已接入通过隔离服验收的 Node 规则版和标准展开路线。女帝王 Lua 已同步更新。公网点名入房与实际出牌记录见 上线验收（本机 `artifacts/amazoness/2026-11-27/full-prelaunch/deployment/online-amazoness.json`）。训练候选保留在本机。猜拳胜出固定选择先攻。标准路线与普通策略共用召唤表示及效果可用性规则。

7911 房间的房主可输入 `/ai 亚马逊`。该入口在 SRVPro2 名单中设为隐藏。牌表沿用「亚马逊人机.ydk」的 40 张主卡、15 张额外和空副卡。

[占位执行器](../Game/AI/Decks/AmazonessExecutor.cs) 支持基础检索、战士长与格斗战士盖放、剑圣融合、部分阻抗及 WindBot 通用战斗。完整刻不知／乔治／鲜花路线、后场时序和斩杀见 [构筑策略](../../skills/ygo-bot-builder/references/amazoness.md)。Windows 试训的命令、样本和验收口径见 [训练计划](TRAINING.md)。

## 2026-11-27 上线验收

- 使用与当前服务器相同的 WindBot 覆盖源码新增独立执行器，向已有服务器名单追加亚马逊。新版 `ygopro-super-pre.ypk` 的 SHA-256 为 `c520e1aa2741166aa3a4bdb395cf4df3672350c59e601dca9a16ccbd9c61de32`。
- 隔离服固定起手见证战士长特召盖陷阱、诅咒师检索格斗战士后融合剑圣、叫声检索战士长三个分支。
- 对手通召一只怪兽时，当前占位版曾以罗刹 C1、救出剧 C2 同连锁响应。救出剧先弹掉唯一对手怪，罗刹随后只完成苏生。这局实际去除为 1.00 分，后续策略须通过双后场交互回归。
- 公网点名召唤成功。随机起手实际完成战士长特召、增援、诅咒师检索、格斗战士特召与盖放、剑圣融合及特召。通过回合边界并结束测试对局。额外核验双子、双子2和大贤者各能点名入房与出牌。
- 上一版本文件的回滚副本位于服务器 `C:\srvpro2-trial\amazoness-seat-20261127-01\before`。

## 完整策略的隔离服入口

[全阶段策略](duel-policy.cjs)在当前引擎提示内选择动作。[运行器](live-server.cjs)使用公开场况和己方手牌，单诅咒师路线只在合法选项匹配时继续，吃坑或动作分歧后改用现有局面。后攻战阶按公开攻守规划，确认可斩时保留攻击手与战斗窗口，非斩杀走进主二并按实际资源续展。[效果审计](EFFECTS.md)记录逐卡条件。

使用独立资源叠层。新版 super pre 的 `script/c23965033.lua` 要以散装覆盖层加载在旧脚本之前。新脚本补齐女王记名；脚本和线上旧指纹（本机 `artifacts/amazoness/2026-11-27/full-prelaunch/card-scripts/manifest.json`）可复核。

```sh
NODE_PATH=/path/to/srvpro2/node_modules node ygo-windbot/amazoness/live-server.cjs \
  --fixture /path/to/srvpro2 --data /path/to/srvpro2/ygopro --data /path/to/card-scripts \
  --game-port 7911 --port 2402
```

用 `GET http://127.0.0.1:2402/healthz` 检查入口。现有 WindBot 和大贤者端口可以继续由各自的服务处理。要把隐藏点名接到该服务，可用 [SRVPro2 preload](srvpro-hook.cjs)，由 `AMAZONESS_LIVE_CONFIG` 指向包含 `fixture`、`gamePort`、`port`、`data` 的 JSON。游戏服的数据路径也需要覆盖旧的女帝王散装脚本，否则侧车读取新卡库与实际对局的脚本不一致。候选模型通过部署门槛后才配置可选的 `model`。
