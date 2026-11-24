---
name: ygo-ypk
description: 构建或检查 YGOPro 自定义卡 .ypk 扩展包。将 cards.json、Lua、卡图、扩展字符串和示例牌表打包，检查卡号冲突及生成的 SQLite CDB。适用于重建 IMPH 示例或制作新的扩展包。
---

# YGOPro 扩展包

定位仓库根目录，读取 `ygo-ypk/README.md` 及其链接的 CDB 字段说明。使用 Python 3.8+ 和现成的 `ygo-ypk/build.py`。

1. 核对输入目录中的 `cards.json`。每张卡要有唯一卡号和名称。按模块文档放置 `script/c<卡号>.lua`、`pics/<卡号>.jpg`、`strings.conf`、`pack/*.ydk` 等可选资源。
2. 给每个需要检查的基础库或其他扩展 CDB 传一个 `--base-cdb`，并使用独立输出路径构建。

   ```sh
   python3 ygo-ypk/build.py ygo-ypk/examples/imph-artmage /tmp/imph2.ypk
   python3 ygo-ypk/build.py /path/to/expansion /path/to/expansion.ypk --base-cdb /path/to/cards.cdb
   ```

3. 检查 ZIP 条目、内嵌 CDB 的 `datas` 和 `texts` 卡号，以及要求的 Lua、卡图和牌表。汇报产物路径、卡牌数和实际执行的冲突检查。Lua 卡效的运行验收在兼容客户端执行。
