---
id: kd_a68d8b6c-2532-4cab-92a0-5b3654a254c8
type: skill
path: paren-note.md
title: paren-note
inheritInjectMode: true
summaryEnabled: true
commandEnabled: true
readOnly: false
inheritAiConfig: true
skillEnabled: true
skillSurface: command
commandTrigger: /paren-note
createdAt: 1784197890038
updatedAt: 1784197890038
---

# paren-note

## Summary
中文括号规则：用户消息中出现在全角括号（）内的内容为补充说明/背景信息，不作为需要执行的命令。加载时机：所有对话默认生效。忽略：半角括号()、英文内容。

## Content
## Instructions

### Core Rule

用户消息中出现在中文全角括号 `（）` 内的内容为**补充说明/背景信息**，不作为需要执行的命令或任务。

- 只识别全角括号 `（）`（Unicode U+FF08, U+FF09）
- 半角括号 `()` 不受此规则影响
- 括号内容可能是：任务背景、上下文补充、作者的思考过程、未来计划等

### 示例

用户消息：`（这个功能后面给 AI 用）修复 BulletManager 的空指针`
- 要执行的任务：修复 BulletManager 的空指针
- 补充说明（不执行）：这个功能后面给 AI 用
