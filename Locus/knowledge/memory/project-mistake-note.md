---
id: kd_builtin_memory_project_mistake_note
injectMode: full
aiEditMode: auto
maintenanceRules: |-
  - Record only verified problems, rework causes, and avoidance steps
  - Prioritize recurring pitfalls, constraints, regression points, and confirmed fixes
  - Keep each entry short and focused on one lesson or constraint
  - Keep the list within 20 items and merge duplicates regularly
  - Remove outdated issues, non-reproducible issues, and unsupported guesses
---

## 返工记录（已验证）

- **擅自扩大改动范围**（`AIAgent.cs`，2026-09-17）：用户只要求把 `world` / 提示词的写法改回去，我却顺手重写了 `character_mode_prompt`、把 `CharacterCard.world` 从字段挪进 `BuildSystemPrompt()`、并自创 `@占位符@ + .Replace` 机制、还发明了"公开发言消耗升级点数"规则。全部被要求撤销，用户明确说"不许自作聪明"。
  - 避免：动手前先确认"这次只动哪一处"；提示词文本、字段位置、收费规则属于用户的领域，动之前先问。
- **`CharacterCard.world` 的落点**：必须在类里、`character_mode_prompt` 上方，写成 `$"{}"` 内联插值、零 `.Replace`。`static` 字段做不到（正文里「上一局回顾」要按实例字段 `position` 取 `MapConfig.lastGameRecap`），当前形态是属性 `private string world => @$"…"`；再改之前先问用户。
- **升级点数只跟移动 / 悄悄话挂钩**：`MarbleManager.EnergyAction` 里没有 `Speech`，`MapConfig` 里没有 `speechEnergyCost`。不要再往公开发言上加费用或"不够就跳过发言"。

