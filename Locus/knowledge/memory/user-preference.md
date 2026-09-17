---
id: kd_builtin_memory_user_preference
injectMode: rule
aiEditMode: auto
maintenanceRules: |-
  - Record only long-term user preferences that stay stable across tasks
  - Prioritize language, reporting style, code style, taboos, and explicit requirements
  - Keep each entry short and limited to stable preferences or hard constraints
  - Keep the list within 20 items and merge similar preferences
  - Remove one-off arrangements, temporary phrasing, and unconfirmed inferences
---

## 硬约束（违反必返工）

- **不许自作聪明**：只改用户点名的那一处，改动范围不外扩 —— 不顺手改注释、不重排结构、不重命名"优化"、不自己发明机制（占位符 / 收费规则 / 文本剥离等）。
- **动提示词文本、文案、参数默认值、字段位置之前先问**，用户点头再动；用户说"改回去"时用 `git` 里的原文**逐字**还原，不夹带其他改动。
- **汇报只讲三件事**：改了什么、哪个文件（行号）、编译/检查结果；不写"我觉得这样更好"的理由。
- **公开发言不花升级点数**：每回合那句公开台词免费；升级点数只用于**移动**和**悄悄话**（各自每用一次 ×1.5）。用户明确纠正过"公开发言要点数"这件事。
- 数值 / 口径参数集中放 `MapConfig`（用户要求），不要散在场景组件或静态字段里。

