# PROJECT_CONTEXT — AI+

## 项目定位
"AI 自动玩弹珠领土游戏"演示项目：4 个 AI 角色（赤喵/苍感/藤延/耶罗）通过 DeepSeek API（function calling + reasoning）自动玩一个实时弹珠领土占领游戏，全程用 AVPro Movie Capture 录制，成品是"AI 们自己打自己"的录制视频。

## 环境
- Unity 2022.3.62f3c1（`ProjectSettings/ProjectVersion.txt`）
- 依赖：Newtonsoft JSON（`com.unity.nuget.newtonsoft-json@3.2.2`）、TMP（本地包 3.2.0-pre.15）、AVProMovieCapture（`Assets/Plugins/RenderHeads`）
- API Key：`Application.persistentDataPath/Key.txt`（`AIAgent.LoadApiKey()`）
- 模型：`deepseek-v4-flash`，API 地址 `https://api.deepseek.com/v1/chat/completions`
- tokenizer.json：放 `StreamingAssets/` 或 `Assets/` 根目录，用于精确 token 计数（`AIRequest.TokenEstimator`）
- 场景：`Assets/Scenes/SampleScene.unity`（主）；`balltest.unity`（小测试）

## 目录结构（Assets）
- `C#/AI/`：AI 请求与调度核心（AIRequest、AIAgent、WhisperManager、DeepSeekCaptureController）
- `C#/实际游戏实体/`：MapConfig（配置/道具执行）、Towel（炮塔/基地）、BulletManager+BulletJobs（Job 化子弹）、AimController、BallPainter、ReactionSystem（AI 工具）、TerritoryCanvas
- `C#/弹珠区/`：MarbleManager、Marble、Shooter、MarbleTrigger、PropTrigger、RotableFilter
- `C#/数据收集/`：InformGetter（每轮情报文本生成）
- `C#/UI显示/`：气泡、武器栏、护盾显示、墙体、抖动/闪光特效等
- `C#/基础支持/`：HugeInt（大数支持.cs）、IStageValue（接口.cs）、SLManager（JSON 存档）、GameEndMonitor、CurveTransform 等
- `C#/视频渲染/`：CapturePause（AVPro 暂停/恢复门面）、AVProPluginCleanup

## 核心循环（AIAgent）
录制开始（CapturePause.IsCapturing）→ 每 `_cycleInterval=2` 秒一轮：
1. 先发一轮并行 AI 请求（边请求边继续录）
2. 到点 `CapturePause.Pause()`，等请求全部完成
3. 恢复录制

每轮请求：`InformGetter.GetInfo` 生成情报（道具栈、炮塔断开提示、最近敌方领土、全阵营场上实体 guid/坐标/数值/速度、各 AI 公开发言、己方弹珠、空槽升级进度、领土面积、受击统计）→ 4 张 CharacterCard 并行 `SendRequest` → 回复中的 assistant content 上气泡（Towel.Say）+ 记入 `InformGetter.AIContents` → 工具调用经 `ReactionSystem.DealToolCallsAsync` 执行。

## AI 工具（ReactionSystem.tools，代码内联定义）
- `use_prop(index, target_guid|aim_x+aim_y, weapon)`：用己方武器栏道具（霰弹/扫射/护盾/大球/任意）
- `control_turret(action=start|stop, target_guid|aim_x+aim_y)`：控制己方炮塔持续瞄准（≤12 秒，距离>4 断开，经 AimController）
- `whisper(to, content)`：给其他 AI 发悄悄话（每 4 回合冷却一次；WhisperManager 处理忙碌排队、互等/环等死锁转投）

## 关键机制
- **角色卡 CharacterCard**（AIAgent 内部类）：4 张卡硬编码在 AIAgent.Start（名字/人设/阵营号/RGBA），system prompt 内嵌游戏规则（领土、弹珠倍乘区 ×2→×4→×8、道具、武器栏 5 格解锁节奏、空槽升级、信息延迟 2 秒）
- **历史管理**：动态压缩（MaybeCompress：前 10 轮不压，之后按成本模型算压缩时机；情报压成"[情报压缩]"，tool 消息改 role=user 保留长度，whisper 内容合并进 content）；保留最近 5 轮（last_round_index）
- **开局狠话**：第 1 轮 tool_choice=none，reasoning 必须带"（我想：…）/（心想：…）"等内心独白标记且 content 无括号，不合格无限重刷（期间暂停录制）；成功开头按人设 MD5 缓存到 `SLManager` 的 `Character/Cache/`，下次复用
- **回复校验**：非开局轮由 AIRequest.ValidateAndMaybeRetry 拦截（无括号、无 [skip]、有思考标记），最多重试 3 次；括号违规额外在 history 追加提醒
- **大球撞击预警**：`InformGetter.AppendBallImpactWarning` 对每个敌方大球做轨迹推演（步长 0.02s、窗口 12s：直线运动 + 地图边界反弹 + 敌方护盾镜面反弹；同队盾无碰撞跳过），输出"预计 t 秒后第一次撞上你的护盾"，含球/盾直径与 guid
- **武器栏 UI**：WeaponsDisplayer 每阵营两套 UIWeaponItem 实例——5 个固定槽实例只显示锁定/倒计时，另 5 个独立道具池实例跟着 PropEntry.id 走（不是槽位换皮）：使用道具后该格播消失动画，其余道具图标用位移动画（moveDuration）滑到前槽，新道具从池取空闲实例摆到目标槽播入场。PropEntry.id 是运行时唯一指纹；两套实例互不抢占
- **遗言**：Towel.Die → AIAgent.OnStageDeathAsync（击杀横幅 + 遗言请求 25 秒超时兜底"无言的告别"）→ 死亡释放所有弹珠/道具/子弹为大球
- **HugeInt**：指数形式大数（2^n），ToShortString 显示
- **存档**：SLManager.ExportToJson/ImportFromJson，CharacterCard 存 `Characters/<名字>`（含 request.messages 即 history）
- **游戏结束**：GameEndMonitor 定期扫描 TerritoryCanvas 领土网格，某阵营涂满整图 → 停 AI 循环、停录制、时间冻结（可配退出）
- **弹珠**：MarbleManager 开局每队 3 颗；空槽升级（AI 模式：空槽每秒积进度，达标生成弹珠，cost×1.5）

## 注意点 / 遗留
- `DeepSeekCaptureController.cs` 是早期测试组件：发一个泛化 RTS 战局分析 prompt（与当前游戏无关），API 地址旧（`/chat/completions` 无 v1），疑似弃用；改 AI 流程时不要误碰它
- `ReactionSystem.stage` 只是兼容旧单阵营入口，实际阵营以 `RequestInfo.toolStage` 为准
- `AIAgent.RunCycleLoop` 里 `TestAIAsyncWithRecord` 先发请求再等 tcs，注释与实现顺序有微妙之处（请求与录制并行，_cycleInterval 到点才暂停）
- `MapConfig.useAIDecision=false` 时道具入栈立即溢出执行（非 AI 演示模式）
- `Towel.Start` 里有 `ShotGun(1048576, 60, 1024)` 开局试射，及 `ShotGunTest`（按 S 手动测试）等调试入口
