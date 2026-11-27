# 人机权重

| 文件 | 用途 |
| --- | --- |
| `Twins2.first-turn-ranker.tsv` | 双子2 的先攻候选排序，见[训练与发布记录](../../artifacts/twins/first-turn-hour-20261127/README.md)。 |
| `Twins2.second-turn-ranker.json` | 双子2 已发布的后攻解场和战斗排序。 |
| `Magistus.first-turn.json`、`Magistus.second-turn.json` | 大贤者侧车的展开权重和后攻权重，见[运行说明](../magistus/README.md)。 |
| `Amazoness.json` | 亚马逊侧车部署过的训练权重。 |
| `Amazoness.lethal-candidate.json` | 亚马逊斩杀训练候选，供回归对照。 |

双子的文件放在 WindBot 可执行文件所在目录的 `Models/` 下。大贤者和亚马逊侧车通过各自运行配置加载模型。

## 统一模型本地构建

```sh
node ygo-windbot/Models/pack-turn-models.cjs /path/to/test-runtime/Models
```

生成的 `Amazoness.turn-ranker.json`、`Magistus.turn-ranker.json` 和 `Twins2.turn-ranker.json` 分别供三套牌加载。大贤者和亚马逊通过各自侧车的 `--model` 参数加载。双子2优先加载可执行文件旁 `Models/` 内的统一模型。打包沿用已有权重初始化，亚马逊旧先后攻权重取均值，新训练入口将两侧样本汇总拟合。已有权重保留供对照。

可战斗回合先比较引擎结算的胜负。未斩杀时比较终场实际解场与剩余阻抗的合计，再比较实伤。先攻沿用现有终场与送抽主分。主目标同分时，各牌组的手牌、手外可用资源、关键时序与抗干扰分别取最多 4、3、2、2 项独立能力。同一用途的候选在对应项目内择一。有效手牌按能否兑现用途计分，公开过、检索到或回收得到均按同一口径。

| 卡组 | 手牌 | 手外资源 | 时机 | 抗干扰 |
| --- | ---: | ---: | ---: | ---: |
| 双子2 | 5 | 2 | 2 | 3 |
| 大贤者 | 5 | 2 | 1 | 3 |
| 亚马逊 | 5 | 3 | 2 | 2 |

训练记录保存子项分值和证据。同前缀主分完全相同的候选在双方重放成功后可生成排序样本。留出对拍用于判断候选权重能否替换当前初始化权重。固定用例见 [决策回归](../tests/README.md#三份录像的决策回归)。
