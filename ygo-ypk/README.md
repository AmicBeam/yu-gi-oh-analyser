# YGOPro 扩展包生成器

使用 Python 3.8+ 标准库，从卡数据、Lua、卡图和其他扩展资源生成 `.ypk`。仓库提供 [IMPH 两卡扩展](../artifacts/ypk/imph2.ypk)和[双子七卡扩展](../artifacts/ypk/twins.ypk)。在仓库根目录执行以下命令即可重建。

```bash
python3 ygo-ypk/build.py ygo-ypk/examples/imph-artmage ygo-ypk/dist/imph2.ypk
python3 ygo-ypk/build.py ygo-ypk/examples/twins ygo-ypk/dist/twins.ypk
```

示例可重建原 v2 Release 的两卡扩展。将生成的包放入客户端游戏目录下的 `expansions/`，启用扩展卡包并重启客户端。已安装旧版示例时，用新版替换旧版，避免重复加载同一卡号。

## 创建自己的扩展

创建一个目录，参考 [示例 cards.json](examples/imph-artmage/cards.json) 编写卡数据。每张卡必填唯一的 `code` 和 `name`，`desc` 是显示卡文，`strings` 最多包含 16 条 Lua 效果提示。用 JSON 整数填写 CDB 数值，`ot` 默认 1，其余数值默认 0。最多可用 `setcode` 指定四个系列编号。连接怪兽用 `linkMarker` 写入连接箭头掩码，灵摆刻度可直接写在 `level` 的高位。

可选资源按以下结构存放，打包时保留相对路径。CDB 由生成器依照 [仓库的 CDB 字段说明](../ygo-db/.ai-docs/ygopro-cdb.md) 生成，文件名取输入目录名。

```text
my-expansion/
├── cards.json
├── strings.conf         # 自定义系列名和其他扩展字符串
├── README.md
├── script/c12345678.lua
├── pics/12345678.jpg
└── pack/sample.ydk
```

可以提供客户端卡库或其他扩展的 CDB 检查卡号冲突。`--base-cdb` 支持重复使用。

```bash
python3 ygo-ypk/build.py my-expansion my-expansion.ypk --base-cdb /path/to/cards.cdb
```

Lua 效果由客户端内核执行，生成器只负责写卡库和组装资源包。
