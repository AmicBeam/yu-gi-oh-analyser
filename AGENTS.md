# YGOAT workspace

Use this map to find the repository skill for the requested task. Skill definitions and their resources live under [skills/](skills/README.md). The entries under `.agents/skills/` point to those same directories.

| Task | Skill | Module |
| --- | --- | --- |
| Analyze a YDK and produce a report | [ygo-deck-analyze](skills/ygo-deck-analyze/SKILL.md) | [Analysis and utilities](skills/README.md) |
| Look up cards or export YDK card data | [ygo-db](skills/ygo-db/SKILL.md) | [ygo-db/](ygo-db/README.md) |
| Download tournament decklists | [ygoprodeck-get-meta](skills/ygoprodeck-get-meta/SKILL.md) | [YGOPRODeck fetcher](skills/ygoprodeck-get-meta/) |
| Build a custom card expansion | [ygo-ypk](skills/ygo-ypk/SKILL.md) | [ygo-ypk/](ygo-ypk/README.md) |
| Build or test the Twins bot | [ygo-windbot](skills/ygo-windbot/SKILL.md) | [ygo-windbot/](ygo-windbot/tests/README.md) |

Read the module's own `AGENTS.md` when present. Use [test-dir-zh-cn/](test-dir-zh-cn/README.md) for the sample YDK and reference analysis. Put new generated output in the task's chosen output directory so the checked-in sample remains available for comparison.
