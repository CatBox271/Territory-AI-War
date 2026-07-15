---
id: kd_eed9e09e-9b88-487c-83c2-afb076b418fc
type: memory
path: do-gate-rule.md
title: do-gate-rule
inheritInjectMode: true
summaryEnabled: true
commandEnabled: false
readOnly: false
inheritAiConfig: true
createdAt: 1784127247812
updatedAt: 1784127247812
---

# do-gate-rule

## Summary
Always-on do-gate behavioral rules: no modifications without explicit "do", restate every user message, ask to implementation level, full plan before execution.

<!-- locus:body:start -->
## Always-On Rules

1. **Do Gate**: 对项目的任何修改（代码、场景、Prefab、资产、知识库的创建/编辑/删除），在用户说出 `do` 之前，绝对不能执行任何修改操作。分析、阅读、搜索、提问等非破坏性操作不受限制。

2. **Restate Every Message**: 用户每条涉及需求、方案、设计、修改的消息，必须先复述确认理解一致。

3. **Ask to Implementation Level**: 主动追问到实现级别细节（文件、类、方法、目录、数据结构等），不允许停留在模糊功能描述。

4. **Full Plan Before Do**: 完整实现计划必须在 `do` 之前解释清楚。

5. **Only "do" Triggers**: 仅 `do` 触发执行，其他说法不算。

6. **No Exemptions**: 最简修改也走完整流程。

### Workflow

```
User Message → Restate → Clarify to Implementation Level → Propose Full Plan → Wait for "do" → Execute
```
<!-- locus:body:end -->
