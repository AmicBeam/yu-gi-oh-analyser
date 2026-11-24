# Yu-Gi-Oh! AI Tools 测试工作目录

这里保留 [示例卡组](test-deck.ydk) 和 [参考报告](test-deck_analysis/report.md)及[最终分析数据](test-deck_analysis/deck_analysis.json)。在仓库根目录打开 Codex，使用 `$ygo-deck-analyze` 分析 `test-dir-zh-cn/test-deck.ydk`。要从头复跑，先把 YDK 复制到独立目录，避免复用样例已有的分析进度。

## Gemini CLI 安装步骤

1. **安装 ygo-db-cli**。从仓库根目录执行。
   ```bash
   (cd ygo-db && go install ./cmd/ygo-db-cli)
   ```

2. **安装支持 skills 的 AI Agent**：
   本示例使用 Gemini CLI，因为它支持 skills 功能且 context window 达到 1M。

3. **安装 skills**：
   ```bash
   gemini skills install https://github.com/AmicBeam/yu-gi-oh-analyser.git --path skills/ --scope workspace
   ```

完成以上步骤后，你就可以开始使用这些 AI 工具来分析你的卡组了！
