---
name: ygo-db
description: 使用 ygo-db-cli 查询游戏王卡片数据库。按卡号、名称、字段（系列）找卡，核对卡文，或把 .ydk 导出为 CSV、JSON 或 YAML。完整卡组报告由 ygo-deck-analyze 编排。
---

# 游戏王卡库查询

使用已安装的 `ygo-db-cli`。如果本机缺少 CLI，在仓库根目录运行 `(cd ygo-db && go run ./cmd/ygo-db-cli --help)` 确认可以运行，再将下面命令中的 `ygo-db-cli` 替换为模块目录内的 `go run ./cmd/ygo-db-cli`。切换目录时使用绝对输入和输出路径。源码要求 Go 1.26.1 或兼容工具链。实际查询会用 Git 拉取或更新本地 MyCard 卡库。

```sh
ygo-db-cli get-card-by-id 14558127 --lang zh-CN --format json
ygo-db-cli get-cards-by-name "灰流丽" --lang zh-CN --format yaml
ygo-db-cli get-cards-by-archetypes "直播☆双子" --lang zh-CN --page 0 --format yaml
ygo-db-cli get-cards-by-ydk -i "/path/to/deck.ydk" -o "/path/to/deck_raw.csv" --format csv
```

- 语言支持 `zh-CN`（默认）和 `en-US`。“字段”和“系列”对应 `archetypes`。怪兽的 `Level` 按类型解释为等级、阶级或 Link 值。
- 名称查询区分精确命中和模糊命中。字段查询支持一至四个参数。列表用 JSON 或 YAML 检查分页，页码从 0 开始。
- 核对标准输出、标准错误和导出内容。找不到卡片时，单卡查询可能仍返回退出码 0。YDK 导出会省略查不到的卡号，按分区比对输入牌号和张数。
- 查询结果注明使用的语言和卡号。需要卡组结构、概率或对局评分时，继续使用 `ygo-deck-analyze`。
