---
id: kd_da6abf16-ce94-49b3-b3df-9ecb7228c157
type: skill
path: do-gate.md
title: do-gate
inheritInjectMode: true
summaryEnabled: true
commandEnabled: true
readOnly: false
inheritAiConfig: true
skillEnabled: true
skillSurface: command
commandTrigger: /do-gate
tools:
- ask_user_question
createdAt: 1784127240251
updatedAt: 1784127240251
---

# do-gate

## Summary
需求对齐门禁：所有修改必须在用户说 do 后才能执行。加载时机：用户需要严格的需求对齐流程时。忽略：不涉及项目修改的纯问答。

## Content
## Instructions

### Core Rules

1. **Do Gate**: 对项目的任何修改（包括代码、场景、Prefab、资产、知识库的创建/编辑/删除），在用户明确说出 `do` 之前，绝对不能执行任何实际修改操作。可以进行分析、阅读、搜索、提问等非破坏性操作。

2. **Restate Every Message**: 用户每条涉及需求、方案、设计、修改的消息，必须先用自己的话复述一遍，确认理解一致后，才能继续提问或说明方案。

3. **Ask to Implementation Level**: 必须主动追问需求细节，一直追问到"具体怎么实现"的粒度。包括但不限于：涉及哪些文件/类、改什么方法/字段、文件放在哪个目录、用什么数据结构、依赖哪些现有系统。不允许停留在模糊的功能描述层面就准备实施方案。

4. **Full Plan Before Do**: 在用户说 `do` 之前，必须把完整的实现计划解释清楚，包括所有修改点、涉及文件、具体改动内容。

5. **Only "do" Triggers Execution**: 只有用户单独发的 `do`（不区分大小写）才能触发执行。其他任何说法（如"开始"、"执行"、"go ahead"、"ok"、"好"、"可以"）都不算，必须继续等待或追问。

6. **No Exemptions**: 即使是最简单的修改（如改一个变量名拼写），也必须走完整流程：复述 → 追问到实现级别 → 说明方案 → 等 `do` → 执行。

### Workflow

```
User Message → Restate → Clarify to Implementation Level → Propose Full Plan → Wait for "do" → Execute
```
