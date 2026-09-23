# 领土 AI 战（Territory AI War）v1.1.0

四个 AI（赤喵 / 青昔 / 氯蚀 / 朝露）在同一张 10×10 的地图上互相涂地、开火、结盟、谈判的自走棋式对战：
每一轮四家各自发一次大模型请求，模型用**工具调用**决定这一轮干什么（用道具 / 移动炮塔 / 控制炮塔 / 登记预行动 / 发悄悄话），
发言、思考、交锋全部由模型自己生成，游戏只负责执行与结算。

- 引擎：Unity 2022.3.62f3c1（内置渲染管线）
- 场景：`Assets/Scenes/SampleScene.unity`
- C# 全部在 `Assets/C#/` 下；AI 相关在 `Assets/C#/AI/`
- 文本渲染用 TextMeshPro（本地包，路径见 `Packages/manifest.json`）

---

## 一、AI 的 Key 放在哪里

全部放在 Unity 的持久化目录（Windows）下，**一行纯文本**、不要引号、不要换行：

```
C:\Users\<你的用户名>\AppData\LocalLow\BoxGame\AI+\
```

| 文件名 | 谁在用 | 说明 |
|---|---|---|
| `Key.txt` | **主模型**：四家 AI 的每一轮请求、遗言 / 获奖感言 / 悄悄话、拟人化、终局记忆、调试采集 | 默认是 DeepSeek 官方 API 的 key。**最少只要这一个文件就能跑起来** |
| `JevKey.txt` | 行为检查（JEV：判断 AI"说了要做却没调工具"） | 单独一把 key；没配也不影响对局，检查会退回 `Key.txt` 那条 ds-flash 路径 |
| `SiliconKey.txt` | ——（已不再使用） | 早期拟人化走硅基流动时的 key，留档用，删掉也没事 |

对局数据、截图、缓存等也都写在这个目录下（`GameStats/`、`Timing/`、`MoveShots/`、`Character/Cache/`），方便排查。

---

## 二、换成你自己的 AI 服务要改哪些地方

所有开关都是**代码里的字段/常量**，改完让 Unity 重新编译即可（部分字段可以在 Inspector 上直接改）。

| 文件 | 成员 | 作用 / 怎么换 |
|---|---|---|
| `Assets/C#/AI/AIAgent.cs` | `ApiUrl`（约 370 行，`private const string`） | **主回合**和遗言 / 感言 / 悄悄话这些旁路请求的接口根地址（默认 `https://api.deepseek.com/v1/chat/completions`）。换成任何 **OpenAI 兼容**的 `/chat/completions` 都能用 |
| 同上 | `BehaviorCheckerModel`（约 2559 行） | 行为检查**退回路径**用的模型名（默认 `deepseek-flash`） |
| 同上 | `LoadApiKey()`（约 413 行） | 读 `Key.txt` 的位置；想换文件名/路径改这里 |
| 同上 | `cardset`（Inspector） | **每张角色卡**（`CharacterCardPreset`）都带 `url` / `model` / `temperature` / `max_tokens` / `thinking`：**四家可以各用各的服务和模型**，卡上的 `url` 会覆盖 `ApiUrl` |
| `Assets/C#/AI/SpeechPolisher.cs` | `ApiUrl`、`Model`、`KeyFileName`、`EnableThinking` | **台词拟人化**那一层（把模型写的【我说】改写成更贴角色的话）。默认走 DeepSeek 官方 `deepseek-flash` + `Key.txt`。想切回硅基流动：`https://api.siliconflow.cn/v1/chat/completions` + `deepseek-ai/DeepSeek-V3.2` + `SiliconKey.txt`，并把 payload 里的 `thinking` 字段改成 `enable_thinking`（注释里写了） |
| `Assets/C#/AI/JEVRequest.cs` | `BaseUrl` / `Model` / `KeyFileName` / `TimeoutSeconds` | 行为检查用的 **TypeSafe System One（JEV）**：默认走中转 `https://aiask.me`，可切官方 `OfficialBaseUrl`；key 读 `JevKey.txt`。整个文件是独立请求层，不用它也可以（见上面的"退回路径"） |
| `Assets/C#/AI/GameMemory.cs` | `Model`、`MaxTokens`、`LoadApiKey()` | 终局把整局经历压成一段长期记忆用的模型（默认 `deepseek-flash`、4096 输出、`Key.txt`） |
| `Assets/C#/AI/DeepSeekCaptureController.cs` | `MODEL`、`max_tokens` | 调试用的采集组件（把某一轮的实际请求/回复存下来便于对照），默认模型名写在常量里；不需要的话把这个组件从场景里去掉即可 |
| `Assets/C#/AI/AIRequest.cs` | `MaxTokens`（上下文上限，默认 1048576）、`RequestTimeoutSeconds` | 换服务时如果对方的**上下文窗口/超时**不一样，改这两个 |

> 提示：
> - 非 DeepSeek 的服务会被自动去掉 DeepSeek 专属字段（`thinking` / `reasoning_effort`）；`stream` 开关在主回合由 `RequestInfo.stream` 单独控制（见 `AIAgent.UntilGreatRequest`）。
> - 提示词里出现的所有数值（升级价格、护盾无敌秒数、道具区概率、视野半径…）都是**运行时现算**的，改 `MapConfig` / 预制体之后不用手改文案。

---

## 三、想改玩法平衡看哪里

- `Assets/C#/实际游戏实体/MapConfig.cs`：地图大小、炮塔移动价格与涨价倍率、悄悄话价格与冷却、护盾无敌基准、穿甲口径、视野截图半径倍率、拟人化开关与间隔……**大部分平衡值都在这一个组件上**。
- `Assets/C#/AI/AIAgent.cs` 的 `world`（提示词正文）：给 AI 的规则/数值说明。
- `Assets/C#/AI/MoveSightCapture.cs`：移动视野截图的取景、抹黑范围、标签样式。
