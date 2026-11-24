# Yu-Gi-Oh! AI Tools

**English** | [简体中文](README-zh.md)

A Yu-Gi-Oh! workspace for deck analysis, card lookup, tournament decklists, custom expansions, and the Twins WindBot.

## Overview

Choose a skill by the result you want. The [skill catalog](skills/README.md) lists the analysis helpers and how to run module checks.

## Workspace

| Module | Skill | Output |
| --- | --- | --- |
| [Deck analysis](skills/README.md) | [ygo-deck-analyze](skills/ygo-deck-analyze/SKILL.md) | Deck structure, opening probabilities, and a gameplay report |
| [Card database](ygo-db/README.md) | [ygo-db](skills/ygo-db/SKILL.md) | Card lookups and YDK data exports |
| [Tournament decklists](skills/ygoprodeck-get-meta/) | [ygoprodeck-get-meta](skills/ygoprodeck-get-meta/SKILL.md) | YGOPRODeck JSON or YDK files |
| [Custom expansions](ygo-ypk/README.md) | [ygo-ypk](skills/ygo-ypk/SKILL.md) | YGOPro `.ypk` packages |
| [Twins WindBot](ygo-windbot/tests/README.md) | [ygo-windbot](skills/ygo-windbot/SKILL.md) | WindBot overlays, a merged CDB, and local regression reports |

Open the repository root in Codex to use the entries in `.agents/skills/`. Definitions and resources live under `skills/`. For a sample deck and reference report, see [test-dir-zh-cn/](test-dir-zh-cn/README.md).

## Features

### Core Analysis Skills

| Skill | Description |
|-------|-------------|
| **ygo-deck-analyze** | The entry point. Coordinates the full pipeline from raw data to final report. |
| **deck-parser** | Extracts engine cores, starters, extenders, and hand traps. Focuses on factual data extraction. |
| **deck-analyzer** | Evaluates deck performance: combo diversity, endboard strength, choke points, and resource loops. |

### Utility Skills

| Skill | Description |
|-------|-------------|
| **starter-probability** | Uses hypergeometric distribution to calculate the odds of drawing specific card combinations. |
| **count-deck** | Quickly counts main, extra, and side deck sizes from YDK files. |
| **simple-calculator** | Performs basic arithmetic. |

## Analysis Workflow

The `ygo-deck-analyze` skill automates the following pipeline:

1.  **Data Retrieval**: Fetch raw card metadata via `ygo-db-cli`.
2.  **Structural Parsing**: Identify starters, extenders, and engine pieces.
3.  **Gameplay Analysis**: Evaluate deck metrics (consistency, ceiling, etc.).
4.  **Reporting**: Generate a formatted `report.md`.

## Output Artifacts

Each analysis run produces:
- `deck_raw.csv`: Raw metadata from the database.
- `deck_parsed.json`: Categorized card roles and engine components.
- `deck_analysis.json`: Quantitative metrics and 6-dimensional scoring.
- `report.md`: Human-readable summary and strategic advice.

## Six-Dimensional Scoring

| Metric | Focus |
|-----------|-------------|
| **Consistency** | Reliability of opening hands / starter density. |
| **Ceiling** | Maximum endboard power when uninterrupted. |
| **Resilience** | Ability to play through hand traps or disruption. |
| **Going Second** | Board-breaking potential and comeback ability. |
| **Grind** | Resource recursion and long-game sustainability. |
| **Diversity** | Variety of combo paths and playstyles. |

## Quick Start

```bash
# Get raw data for a deck
ygo-db-cli get-cards-by-ydk -i deck.ydk -o deck_raw.csv

# Run analysis via Gemini CLI / Claude Code
# Use the 'ygo-deck-analyze' skill on the generated CSV
```

## Requirements

- **AI Skills**: Gemini CLI, Claude Code, or compatible AI agent environments.
- **ygo-db**: Go 1.26.1 toolchain and Git.
- **Python**: 3.14+ for the shared skills test environment. The YPK builder runs on Python 3.8+.

## License

Apache 2.0. This repository starts from a snapshot of [Yu-Gi-Oh! AI Tools](https://github.com/elmhuangyu/yu-gi-oh-ai-tools).

---

*Yu-Gi-Oh! is a trademark of Konami Digital Entertainment. This project is not affiliated with, endorsed, sponsored, or specifically approved by Konami Digital Entertainment.*
