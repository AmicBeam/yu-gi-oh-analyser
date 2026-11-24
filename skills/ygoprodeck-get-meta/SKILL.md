---
name: ygoprodeck-get-meta
description: 从 YGOPRODeck 批量获取 TCG、OCG 或 Genesys 赛事牌表，支持按起始日期筛选并落盘为 JSON 或 YDK。适用于下载上位卡组、环境牌表和赛事构筑样本。
---

# 下载 YGOPRODeck 赛事牌表

使用本技能目录下的 `scripts/fetch_meta_deck.py`。该脚本需要 Python 3.10+ 和网络访问，自动按偏移量分页。根据请求选择 `tcg`、`ocg` 或 `genesys`，省略起始日期时脚本使用约一个月前。

从仓库根目录执行，按需要替换格式和输出路径。技能单独安装时，脚本路径使用实际技能目录下的 `scripts/fetch_meta_deck.py`。

```sh
python3 skills/ygoprodeck-get-meta/scripts/fetch_meta_deck.py --format ocg -o /path/to/decks.json
python3 skills/ygoprodeck-get-meta/scripts/fetch_meta_deck.py --format ocg --ydk_dir /path/to/decks
```

用 `--from YYYY-MM-DD` 指定起始日期。每次执行选择一种输出，两个参数同时存在时脚本会优先写 YDK。确保 JSON 的父目录存在。导出 YDK 时选择空目录或新目录，避免覆盖同名文件。

核对产物数量和格式，汇报数据来源、赛制、请求日期范围和输出路径。需要单套牌的完整评估时，把对应的 YDK 交给 `ygo-deck-analyze`。
