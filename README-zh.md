# Yu-Gi-Oh! AI Tools (游戏王 AI 分析工具集)

[English](README.md) | **简体中文**

用于游戏王卡组分析、卡库查询、赛事牌表下载、自定义扩展和双子人机的工具集。

## 项目简介

按需要的产物选择技能。[技能索引](skills/README.md)列出了分析子技能和模块验收命令。

## 工作区

| 模块 | 技能入口 | 产物 |
| --- | --- | --- |
| [卡组分析](skills/README.md) | [ygo-deck-analyze](skills/ygo-deck-analyze/SKILL.md) | 卡组结构、起手概率和对局报告 |
| [卡牌数据库](ygo-db/README.md) | [ygo-db](skills/ygo-db/SKILL.md) | 卡文查询和 YDK 卡数据导出 |
| [赛事牌表采集](skills/ygoprodeck-get-meta/) | [ygoprodeck-get-meta](skills/ygoprodeck-get-meta/SKILL.md) | YGOPRODeck 的 JSON 或 YDK 文件 |
| [自定义扩展包](ygo-ypk/README.md) | [ygo-ypk](skills/ygo-ypk/SKILL.md) | YGOPro `.ypk` |
| [双子人机](#双子人机) | [ygo-windbot](skills/ygo-windbot/SKILL.md) | WindBot 覆盖文件、合并卡库和本地回归报告 |

在 Codex 中打开仓库根目录即可使用 `.agents/skills/` 下的技能入口。技能正文和资源统一维护在 `skills/`。示例卡组、分析产物和上手命令见 [test-dir-zh-cn/](test-dir-zh-cn/README.md)。

## 功能特性

### 核心分析 Skills

| Skill | 描述 |
|-------|-------------|
| **ygo-deck-analyze** | 分析流程总控。协调从原始数据获取到最终报告生成的完整流水线。 |
| **deck-parser** | 事实提取引擎。识别系统轴、展开点、补点及手坑，并计算基础展开概率。 |
| **deck-analyzer** | 强度评估引擎。分析展开多样性、终场强度、卡点 (Chokepoint) 及资源循环。 |

### 实用工具 Skills

| Skill | 描述 |
|-------|-------------|
| **starter-probability** | 使用超几何分布计算在 5 张起手手中抽到特定组合的概率。 |
| **count-deck** | 快速统计 YDK 文件中主卡组、额外卡组和副卡组的张数。 |
| **simple-calculator** | 四则运算。 |

## 分析流水线

`ygo-deck-analyze` Skill 将自动执行以下流程：

1.  **数据采集**: 通过 `ygo-db-cli` 获取原始卡片元数据。
2.  **结构解析**: 识别展开点、核心组件和防御性卡片。
3.  **强度评估**: 对卡组稳定性、上限等多个维度进行量化评分。
4.  **生成报告**: 汇总结果并生成格式化的 `report.md`。

## 输出成果

每次分析将生成以下文件：
- `deck_raw.csv`: 从数据库导出的原始元数据。
- `deck_parsed.json`: 结构化卡组信息（系统轴、展开点、手坑分布）。
- `deck_analysis.json`: 多维度评分与详细分析结论。
- `report.md`: 供人类阅读的分析报告及战术建议。

## 六维评分体系

| 维度 | 评估重点 |
|-----------|-------------|
| **稳定性 (Consistency)** | 起手拿到展开点的概率。 |
| **上限 (Ceiling)** | 无干扰情况下的终端压制力。 |
| **抗干扰 (Resilience)** | 吃手坑后的补点能力与妥协场。 |
| **后攻 (Going Second)** | 突破对方场面及返场能力。 |
| **续航 (Grind)** | 资源回收与长线作战能力。 |
| **多样性 (Diversity)** | 展开路线的灵活性与多变性。 |

## 快速上手

```bash
# 生成卡组的原始数据
ygo-db-cli get-cards-by-ydk -i deck.ydk -o deck_raw.csv

# 在 Gemini CLI / Claude Code 中运行
# 激活 'ygo-deck-analyze' Skill 处理上述生成的 CSV
```

## 双子人机

房主可以在 SRVPro2 的普通房间聊天里输入 `/AI` 添加人机，也可以用 `/AI 双子` 指定这套卡组。[仓库中的人机牌表](ygo-windbot/Decks/AI_Twins.ydk) 使用「双子 (2)」狮鹫版的主卡和额外卡组（3 霜精、3 璃拉原皮、1 秘密口令句、2 麻烦桑），副卡组为空。

[双子执行器](ygo-windbot/Game/AI/Decks/TwinsExecutor.cs) 面向 [SRVPro2 使用的 WindBot 分支](https://code.moenext.com/nanahira/windbot)，已在提交 `6abb46a36a1da10fa37521ff2bb37c63aa90fcb9` 上构建。人机按起手、已用效果和可用站位选择开局表或搜索路线，再按终场得分排序。引擎在镇魂棺落地时给出快速效果窗口，执行器会优先发动并解放镇魂棺作为代价。

复现构建时，把 `ygo-windbot` 下的 `Game` 和 `Decks` 中的文件放到 WindBot 对应目录，应用 [回环监听补丁](ygo-windbot/loopback.patch)，然后构建 Release。把 [bot 名单](ygo-windbot/bots.json) 和 `cards.cdb` 放到运行目录。卡库可用 [合并脚本](ygo-windbot/merge-cards.py) 从服务器基础库和扩展 YPK 构建，参数中的 YPK 按加载优先级从低到高传入。

本地开局、手坑对拍和评分验收见 [WindBot 回归文档](ygo-windbot/tests/README.md)。

## 环境要求

- **AI Skills**: Gemini CLI、Claude Code 或兼容的 AI 助手环境。
- **ygo-db** 需要 Go 1.26.1 工具链和 Git。
- **Python** 技能测试环境需要 3.14+。YPK 构建脚本支持 3.8+。

## 许可证

Apache 2.0。本仓库基于 [Yu-Gi-Oh! AI Tools](https://github.com/elmhuangyu/yu-gi-oh-ai-tools) 的工作区快照。

---

*游戏王是科乐美数字娱乐公司的商标。本项目与科乐美数字娱乐公司无关联、未被其认可、赞助或特别批准。*
