# 人机权重

| 文件 | 用途 |
| --- | --- |
| `Twins2.first-turn-ranker.tsv` | 双子2 的先攻候选排序，见[训练与发布记录](../../artifacts/twins/first-turn-hour-20261127/README.md)。 |
| `Twins2.second-turn-ranker.json` | 双子2 已发布的后攻解场和战斗排序。 |
| `Magistus.first-turn.json`、`Magistus.second-turn.json` | 大贤者侧车的展开权重和后攻权重，见[运行说明](../magistus/README.md)。 |
| `Amazoness.json` | 亚马逊侧车部署过的训练权重。 |
| `Amazoness.lethal-candidate.json` | 亚马逊斩杀训练候选，供回归对照。 |

双子的文件放在 WindBot 可执行文件所在目录的 `Models/` 下。大贤者和亚马逊侧车通过各自运行配置加载模型。
