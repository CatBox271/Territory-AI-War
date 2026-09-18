using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 行动系统：把游戏内的可用行动包装成 AI 可调用的 Function Tools。
/// 开放工具：use_prop（使用自己阵营武器栏里的道具，并可选朝向）、control_turret（启停自己炮塔的持续瞄准，复用 AimController）。
/// 道具执行走 MapConfig.ExecutePropEffect；炮塔控制只走 AimController，不新增其他游戏性逻辑。
/// </summary>
public class ReactionSystem : MonoBehaviour, Itool
{
    [Tooltip("当前调用工具的角色阵营（由 AIAgent 在请求前写入 CharacterCard.position）")]
    public int stage = -1;

    /// <summary>AI 调用道具的初始瞄准误差基准（度）：0 级时是 ±这么多。</summary>
    public const float BaseAimAngleError = 15f;
    /// <summary>每级炮塔强化把道具瞄准误差缩小到 1/该值（升级选择卡片上的数值也读它，别只改一边）。</summary>
    public const float AimErrorShrinkPerLevel = 2f;

    [System.NonSerialized] // 工具表以代码内联定义为准，不进场景序列化（object 参数无法被 Unity 序列化）
    public List<Tool> tools = new List<Tool>
    {
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = UsePropToolName,
                description = "使用自己阵营武器栏中指定格子的道具。index 从 1 开始：1=第 1 格，2=第 2 格，以此类推。需要朝目标射击时，优先传 target_guid（场上信息里的 guid）；没有 guid 时传 aim_x 和 aim_y 指定地图世界坐标。使用成功后道具立即消耗并生效。没有道具或 index 超过当前持有数量时不要调用。如果该道具是【任意】，可以传 weapon 从 " + string.Join("、", MapConfig.ConcreteWeaponNames()) + " 里指定实际触发的武器；不传则随机。一次使用多个道具时，请按武器栈从后往前（高 index  低 index）依次调用，减少槽位反复移动。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "index",
                            new
                            {
                                type = "integer",
                                description = "要使用的道具格子序号，从 1 开始，最大不能超过当前道具数量。"
                            }
                        },
                        {
                            "weapon",
                            new
                            {
                                type = "string",
                                @enum = MapConfig.ConcreteWeaponNames(),
                                description = "仅当目标道具是【任意】时生效：指定实际要触发的武器，可选值：" + string.Join("、", MapConfig.ConcreteWeaponNames()) + "。其他道具不要传。"
                            }
                        },
                        {
                            "target_guid",
                            new
                            {
                                type = "string",
                                description = "要瞄准的目标 guid，取自已收到的场上信息。优先使用；传了 target_guid 就不用传 aim_x/aim_y。"
                            }
                        },
                        {
                            "aim_x",
                            new
                            {
                                type = "number",
                                description = "瞄准点的世界坐标 x。只有不传 target_guid 时使用，且必须与 aim_y 同时提供。"
                            }
                        },
                        {
                            "aim_y",
                            new
                            {
                                type = "number",
                                description = "瞄准点的世界坐标 y。只有不传 target_guid 时使用，且必须与 aim_x 同时提供。"
                            }
                        }
                    },
                    required = new List<string> { "index" }
                }
            }
        },
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = MergePropToolName,
                description = "把自己武器栏里的两个**同种**道具合并成一个：数值相加、占一个槽位、腾出另一个槽位。免费，不消耗能量，也不改变总数值。注意：**无脑合并不一定是好事**——有些道具你根本用不了那么多，有些分开用收益最大（两颗大球朝两个方向、两发霰弹打两个目标、两次护盾分两轮顶伤害）。槽位不紧张、或者确实需要一次性堆出大数值时再合。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "index_a",
                            new
                            {
                                type = "integer",
                                description = "第一个道具的格子序号，从 1 开始。"
                            }
                        },
                        {
                            "index_b",
                            new
                            {
                                type = "integer",
                                description = "第二个道具的格子序号，从 1 开始；必须与 index_a 是**同一种**武器，否则合并会被拒绝。"
                            }
                        }
                    },
                    required = new List<string> { "index_a", "index_b" }
                }
            }
        },
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = ControlTurretToolName,
                description = "控制自己炮塔的持续瞄准。action=start 开始控制并让炮塔持续转向目标；必须传 target_guid（场上信息里的 guid），或 aim_x+aim_y（世界坐标）。action=stop 停止控制，炮塔立即恢复自动旋转。这个工具不消耗道具、不发射子弹，只控制炮塔朝向；开始控制后最多持续12秒，目标距离超过4也会自动断开；需要继续保持时再次调用 start。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "action",
                            new
                            {
                                type = "string",
                                @enum = new List<string> { "start", "stop" },
                                description = "start=开始控制炮塔瞄准目标；stop=停止控制并恢复自动旋转。"
                            }
                        },
                        {
                            "target_guid",
                            new
                            {
                                type = "string",
                                description = "start 时优先使用的瞄准目标 guid，取自已收到的场上信息；传了 target_guid 就不用传 aim_x/aim_y。stop 不需要。"
                            }
                        },
                        {
                            "aim_x",
                            new
                            {
                                type = "number",
                                description = "start 时瞄准点的世界坐标 x，与 aim_y 必须同时提供；stop 不需要。"
                            }
                        },
                        {
                            "aim_y",
                            new
                            {
                                type = "number",
                                description = "start 时瞄准点的世界坐标 y，与 aim_x 必须同时提供；stop 不需要。"
                            }
                        }
                    },
                    required = new List<string> { "action" }
                }
            }
            }
        ,
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = MoveTurretToolName,
                description = "移动自己的炮塔（两步确认）：第一次只预览、不会移动——给方向（angle 角度，或 dir_x+dir_y 向量，会自动归一化）与 distance 距离，工具算出目标点并返回；看清返回里的目标点和警告后，第二次调用不再给方向距离，只传 confirm=true 才会真正开始移动。每次移动消耗升级能量（与距离无关，具体数值见预览返回；同一个动作每用一次价格 ×涨价倍率，越移越贵），能量不足则**连预览都不给**，也就不会附带视野截图。炮塔按固定速度直线移动，到达目标、撞到地图边界或超时会停下并通知你；distance=0 表示原地不动（用来停下）。**移动途中不能改道**：一旦开始走就会走到底，想换方向只能等它停下再走一次（途中想立刻停住，可以用 distance=0 做一次预览+确认）。移动本身不会主动广播你的新位置（对方要么靠大球/子弹撞上你护盾或炮塔本体时的撞击情报反推，要么你正好落进他 move_turret 预览附带的视野截图里被看到）。距离由你自己定：最大移动距离是上限、不是必须走满；确认前先核对目标点是否靠近各炮塔初始位置或撞击情报推测出的敌方方位，炮塔重叠不会被弹开、也不会自动停下，停到别人身上等于把命送出去。预览时还会附一张以你炮塔为圆心、半径等于你最大移动距离的圆形俯视截图（整个可移动范围都在图里，黑色环带 = 圆形视野之外，暗红色 = 地图之外），图上会直接标出周围每个球的位置、朝向和数值；先看图再决定方向和距离。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        { "angle", new { type = "number", description = "方向角度（度）：0=地图右(+X)，逆时针为正，90=上。与 dir_x/dir_y 二选一，第一次调用时必给一个。" } },
                        { "dir_x", new { type = "number", description = "方向向量 x（必须与 dir_y 同时给，会自动归一化）。与 angle 二选一。" } },
                        { "dir_y", new { type = "number", description = "方向向量 y（必须与 dir_x 同时给，会自动归一化）。与 angle 二选一。" } },
                        { "distance", new { type = "number", description = "移动距离（世界单位，地图总宽10）。第一次调用必给；0=原地不动（停下）。" } },
                        { "confirm", new { type = "boolean", description = "true = 执行上一次预览的移动。第二次调用只传这个，不要再传方向与距离。" } }
                    },
                    required = new List<string> { }
                }
            }
        },
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = WhisperToolName,
                  description = "给另一个AI发送悄悄话并等待对方回复。to=对方阵营编号(1-4，不能是自己)；content=悄悄话内容，请用对方的名字称呼对方（玩家名单见系统提示）。对方空闲时立即回复；对方忙碌时会等对方忙完再单独回复。**要消耗升级能量、并且有回合冷却（具体数值见每轮情报），能量与移动共用同一个池子**。如果出现互相等待或环形等待，系统会自动调配，返回结果里会说明；被系统调配终止的会退还这次冷却与能量。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "to",
                            new
                            {
                                type = "integer",
                                description = "接收悄悄话的AI阵营编号，1-4，不能是自己的阵营。"
                            }
                        },
                        {
                            "content",
                            new
                            {
                                type = "string",
                                description = "悄悄话内容，尽量简短明确。"
                            }
                        }
                    },
                    required = new List<string> { "to", "content" }
                }
            }
        },
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = ReadyActionToolName,
                description = "预行动：登记一个「等条件满足后再替我执行」的工具调用，并管理已登记的预行动。"
                    + "action=add 时必填 tool_name（触发时调用哪个工具）与 ready（条件串数组）；"
                    + "action=list 列出当前全部预行动（含是否满足、已执行几次、失败记录）；"
                    + "action=remove 需要 id；action=clear 清空；action=pause/resume 需要 id。"
                    + "ready 的写法（每条字符串用 -- 分词）："
                    + "① 条件：all--对象--比较--值（any-- 任一满足、not-- 取反）；对象有 upgrade(升级能量) left(空槽数) slot(槽位上限) shield(护盾值) bullet(子弹量) marble(弹珠数) territory(领土像素) threat(最近敌方领土距离) threatPos--x/y(最近敌方领土坐标) round(回合数) enemyNum(存活敌人数) moveCost/whisperCost(本次价格) pos--x/y(自己坐标) moving--is--true/false prop--contain--种类--value--比较--值 prop--count--比较--值 warn--count--比较--值 warn--type--穿甲/大球--eta--比较--秒；比较 = more/less/moreEqual/lessEqual/equal。"
                    + "② select--prop--…：触发时挑哪一格道具（contain--种类--value--比较--值 / smallest / biggest / index--N），结果自动填进 index。"
                    + "③ para--参数名--值：触发时覆盖/补充这次工具调用的参数（如 para--aim_x--0、para--angle--0、para--distance--1.5、para--weapon--大球、para--to--3、para--content--……）。"
                    + "④ reuse--0/-1/N：0=执行一次后自动取消（默认）、-1=永久、N=存活 N 个 AI 轮次。"
                    + "例：想「槽满时自动把最小的一颗大球打向(0,0)腾格子」就 add → tool_name=use_prop，ready=[\"all--left--less--1\",\"all--prop--contain--大球--value--less--1048576\",\"select--prop--contain--大球--value--less--1048576\",\"para--aim_x--0\",\"para--aim_y--0\"]。"
                    + "每个阵营最多 5 条；执行失败会保留并在每轮情报里合并显示失败原因。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "action",
                            new
                            {
                                type = "string",
                                @enum = new List<string> { "add", "list", "remove", "clear", "pause", "resume" },
                                description = "add=登记新预行动（默认）；list=列出；remove=按 id 删除；clear=清空；pause/resume=暂停或恢复某条。"
                            }
                        },
                        {
                            "tool_name",
                            new
                            {
                                type = "string",
                                @enum = new List<string> { "use_prop", "merge_prop", "move_turret", "control_turret", "whisper" },
                                description = "add 时必填：触发时实际要调用的工具名。"
                            }
                        },
                        {
                            "ready",
                            new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "add 时必填：条件与动作串数组，见工具说明里的 ①②③④ 写法。"
                            }
                        },
                        {
                            "reuse",
                            new
                            {
                                type = "integer",
                                description = "0=执行一次后自动取消（默认）、-1=永久保留、N=存活 N 个 AI 轮次。"
                            }
                        },
                        {
                            "id",
                            new
                            {
                                type = "integer",
                                description = "remove / pause / resume 时要操作的那条预行动的 id（list 里能看到）。"
                            }
                        }
                    },
                    required = new List<string> { "action" }
                }
            }
        }
    };

    private const string UsePropToolName = "use_prop";
    /// <summary>合并道具：把两个同种道具并成一个（数值相加）。</summary>
    private const string MergePropToolName = "merge_prop";
    /// <summary>预行动：登记"等条件满足再执行"的工具调用（唯一入口，见 ReadyActionManager）。</summary>
    private const string ReadyActionToolName = ReadyActionManager.ToolName;

    /// <summary>场景里的那个实例：预行动系统要拿它来直接执行工具。</summary>
    public static ReactionSystem Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }
    private const string ControlTurretToolName = "control_turret";
    private const string MoveTurretToolName = "move_turret";
    private const string WhisperToolName = "whisper";

    /// <summary>移动预览缓存：阵营 -> 上一次预览的方向与距离（4 个 AI 并行请求，不能共用一个字段）。</summary>
    private static readonly Dictionary<int, MovePreview> pendingMoves = new();
    /// <summary>预览有效期（秒）：过期后 confirm 会被拒绝，要求重新预览。</summary>
    private const float MovePreviewValidSeconds = 60f;

    /// <summary>移动途中不许改道（这条机制已经去掉）时统一返回的说明。</summary>
    private const string MovingNoRedirectText =
        "移动炮塔失败：炮塔正在移动中，不能改道——移动一旦开始就会一直走到底（到达目标 / 撞到地图边界 / 超时才停）。"
        + "想换方向请等它停下再走一次；如果现在就想让它停住，可以用 distance=0 做一次预览 + confirm 让它原地停下。";

    private class MovePreview
    {
        public Vector2 dir;
        public float distance;
        public float time;
    }

    [Tooltip("当前回合数，由 AIAgent 在每轮并行请求前写入，用于 whisper 每4回合冷却")]
    public int currentRound;

    // 坐标瞄准用的隐藏锚点：不创建实体，只提供一个 Transform 给既有 AimController 持续跟踪。
    private Transform aimAnchor;


    /// <summary>
    /// Itool 实现：AI 每发起一轮 tool_calls，AIRequest 会把这些调用传进来。
    /// 每个 ToolCall 必须返回一条 role=tool 的消息，并带原 tool_call_id。
    /// </summary>
    public async Task<List<DeepSeekMessage>> DealToolCallsAsync(List<ToolCall> toolCalls, int toolStage = -1)
    {
        List<DeepSeekMessage> result = new List<DeepSeekMessage>();
        if (toolCalls == null) return result;

        int callStage = toolStage >= 0 ? toolStage : stage;
        var sights = new List<DeepSeekMessage>();
        foreach (ToolCall call in toolCalls)
        {
            DeepSeekMessage toolMsg = await DealToolCall(call, callStage, sights);
            if (toolMsg != null) result.Add(toolMsg);
        }

        // 截图统一放到所有 tool 消息之后：role=tool 的消息必须紧跟 assistant(tool_calls)，
        // 中间插一条 user 会让 API 认为工具回执不成对（严格实现直接 400）。
        result.AddRange(sights);
        return result;
    }

    /// <summary>
    /// Debug 用：每个阵营这一批工具调用各自的耗时（毫秒，系统时间）。
    /// 4 张卡是并行请求的，所以按阵营分开存；AIAgent 在回合结束时 TakeToolTimings(stage) 取走并清空。
    /// </summary>
    private static readonly Dictionary<int, List<string>> toolTimings = new();

    /// <summary>取走某阵营这段时间里记下的工具耗时（取完就清空）。</summary>
    public static List<string> TakeToolTimings(int callStage)
    {
        if (toolTimings.TryGetValue(callStage, out List<string> list))
        {
            toolTimings.Remove(callStage);
            return list;
        }
        return new List<string>();
    }

    private static void RecordToolTiming(int callStage, string toolName, double ms, bool ok)
    {
        if (!toolTimings.TryGetValue(callStage, out List<string> list))
        {
            list = new List<string>();
            toolTimings[callStage] = list;
        }
        list.Add($"{toolName} {ms:0}ms{(ok ? "" : "（失败）")}");
    }

    private async Task<DeepSeekMessage> DealToolCall(ToolCall call, int callStage, List<DeepSeekMessage> sights)
    {
        ToolOutcome outcome;
        bool movePreviewCreated = false;

        // Debug 耗时：从进来到这一条 tool 回执拼完，含 whisper 等对方回话、含移动预览附带的截图
        System.Diagnostics.Stopwatch toolWatch = System.Diagnostics.Stopwatch.StartNew();

        if (call == null)
        {
            outcome = new ToolOutcome("工具调用失败：ToolCall 为空。");
        }
        else if (call.function == null || string.IsNullOrEmpty(call.function.name))
        {
            outcome = new ToolOutcome("工具调用失败：缺少 function.name。");
        }
        else if (call.function.name == UsePropToolName)
        {
            outcome = UseProp(call.function.arguments, callStage);
        }
        else if (call.function.name == MergePropToolName)
        {
            outcome = MergeProp(call.function.arguments, callStage);
        }
        else if (call.function.name == ReadyActionToolName)
        {
            outcome = ReadyAction(call.function.arguments, callStage);
        }
        else if (call.function.name == ControlTurretToolName)
        {
            outcome = ControlTurret(call.function.arguments, callStage);
        }
        else if (call.function.name == MoveTurretToolName)
        {
            outcome = MoveTurret(call.function.arguments, callStage, out movePreviewCreated);
        }
        else if (call.function.name == WhisperToolName)
        {
            outcome = await Whisper(call.function.arguments, callStage);
        }
        else
        {
            outcome = new ToolOutcome($"未知工具：{call.function.name}");
        }

        // 玩家侧只飘 outcome.action（AI 做了什么），工具返回的 outcome.result 只回给模型。
        // 调用失败时 action 为空，这一条就不飘字：没做成的事没必要播。
        ShowActionText(callStage, outcome.action);

        // 移动预览额外附一张炮塔视野截图：图片只能放在 user 消息里，
        // 先攒到 sights，等这一批 tool 消息都发完再统一追加（见 DealToolCallsAsync）。
        if (movePreviewCreated) AppendMoveSight(sights, callStage);

        // Debug 耗时：到这里这一条工具（含 whisper 等回话、含刚附上的截图）就算完了
        toolWatch.Stop();
        RecordToolTiming(callStage, call?.function?.name ?? "（空调用）", toolWatch.Elapsed.TotalMilliseconds,
            !string.IsNullOrEmpty(outcome.action));

        return new DeepSeekMessage
        {
            role = "tool",
            content = outcome.result,
            tool_call_id = call != null ? call.id : ""
        };
    }

    /// <summary>
    /// 给这次移动预览附一张「以炮塔为圆心、半径 = 0.6 × 最大移动距离」的圆形俯视图。
    /// 截图是临时的：这一轮结束后 AIAgent 会把 contentBlocks 清掉、只留文字，不会每轮重复上传。
    /// 截图失败不影响移动流程，只打一条 warning。
    /// </summary>
    private static void AppendMoveSight(List<DeepSeekMessage> messages, int callStage)
    {
        if (!MoveSightCapture.IsEnabled) return;
        if (!Towel.AllTowel.TryGetValue(callStage, out Towel towel) || towel == null) return;

        // 先清掉上一张：同一轮里连续预览好几次时只带最新那张，省 token。
        // 只清自己这一张卡 —— 四张卡是并行请求的，清全部会把别人正在用的截图抹掉。
        AIAgent.DropMoveShotImages(callStage);

        MoveSightCapture.Shot shot = MoveSightCapture.Capture(towel, callStage);
        if (shot == null || !shot.ok)
        {
            Debug.LogWarning($"[移动截图] stage {callStage} 生成失败：{shot?.error}");
            return;
        }

        string text =
            $"【移动视野】这是以你的炮塔为圆心、半径 {shot.radius:0.00} 的俯视截图" +
            $"（半径 = 你当前最大移动距离 {shot.maxMove:0.00} 的 {MoveSightCapture.RadiusFactor:0.##} 倍）。" +
            "圆心就是你炮塔的当前位置；图片上方 = +y（北），右方 = +x（东）。" +
            "图中黑色环带是圆形视野之外、暗红色区域是地图之外——那两种地方没有信息，也去不了。" +
            "先看清附近有没有别的炮塔 / 护盾 / 大球，再决定方向和距离（不必走满最大距离）。";

        var msg = new DeepSeekMessage { role = "user", content = text };
        msg.contentBlocks = new List<DeepSeekContentBlock>
        {
            DeepSeekContentBlock.Text(text),
            DeepSeekContentBlock.Image(shot.dataUrl)
        };
        messages.Add(msg);

        Debug.Log($"[移动截图] stage {callStage} 半径 {shot.radius:0.00}（{shot.size}x{shot.size}，" +
                  $"PNG {shot.png?.Length ?? 0} 字节）已存：{shot.savedPath}");
    }

    /// <summary>工具行动的出口：把拼好的 action 交给 AIAgent.ShowAction（当前不显示）。</summary>
    private static void ShowActionText(int callStage, string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        AIAgent.ShowAction(callStage, action);
    }

    /// <summary>瞄准目标的人类可读写法：只给坐标，例如“(1.2, 3.4)”。</summary>
    private static string DescribeAimTarget(ItemType aim)
    {
        if (aim == null) return "";
        return aim.pos.ToString();
    }


    /// <summary>使用道具：消耗指定槽位，并连同瞄准目标交给既有游戏逻辑 ExecutePropEffect 执行。</summary>
    /// <summary>
    /// 合并道具：把自己武器栏里的两个**同种**道具并成一个（数值相加、占住靠前的那个槽位、腾出另一个）。
    /// 免费：不消耗能量、不改变总数值。合并后这一格用一个**新 id**，武器栏 UI 会把它当新道具重新显示数值。
    /// </summary>
    private ToolOutcome MergeProp(string argumentsJson, int callStage)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return new ToolOutcome("合并道具失败：游戏配置(MapConfig)不存在。");
        if (callStage < 0 || callStage >= config.teamProps.Length)
            return new ToolOutcome($"合并道具失败：当前角色阵营 {callStage} 无效。");

        List<PropEntry> props = config.teamProps[callStage];
        if (props == null)
            return new ToolOutcome($"合并道具失败：{callStage} 号阵营没有道具栏。");

        MergePropArguments args = null;
        try { args = JsonConvert.DeserializeObject<MergePropArguments>(argumentsJson); } catch { }
        if (args == null)
            return new ToolOutcome("合并道具失败：参数无法解析，需要 index_a 与 index_b（两个格子序号，从 1 开始）。");

        if (args.index_a == args.index_b)
            return new ToolOutcome("合并道具失败：index_a 和 index_b 不能是同一格。");
        if (args.index_a < 1 || args.index_a > props.Count || args.index_b < 1 || args.index_b > props.Count)
            return new ToolOutcome($"合并道具失败：格子序号超出范围（当前共 {props.Count} 格道具）。");

        PropEntry a = props[args.index_a - 1];
        PropEntry b = props[args.index_b - 1];
        if (a == null || b == null)
            return new ToolOutcome("合并道具失败：那一格是空的。");
        if (a.item != b.item)
            return new ToolOutcome($"合并道具失败：只能合并**同种**道具（第 {args.index_a} 格是 {a.item}，第 {args.index_b} 格是 {b.item}）。");

        HugeInt sum = a.value + b.value;
        int keep = Mathf.Min(args.index_a, args.index_b);                 // 留在靠前的槽位，顺序不乱
        int drop = Mathf.Max(args.index_a, args.index_b);
        props.RemoveAt(drop - 1);
        props.RemoveAt(keep - 1);
        props.Insert(keep - 1, new PropEntry { item = a.item, value = sum, stage = callStage });

        return new ToolOutcome(
            $"已合并：第 {args.index_a} 格 + 第 {args.index_b} 格 → {a.item} {sum.ToShortString()}（现在共 {props.Count} 格道具）。"
            + "合并免费、总数值不变；但分开用有时更划算（两个不同目标、两轮不同时间），需要分着用就别合。",
            $"【合并道具】{a.item} {sum.ToShortString()}");
    }

    /// <summary>预行动工具：登记 / 列出 / 删除 / 清空 / 暂停 / 恢复（逻辑都在 ReadyActionManager）。</summary>
    private ToolOutcome ReadyAction(string argumentsJson, int callStage)
    {
        ReadyActionArguments args = null;
        try { args = JsonConvert.DeserializeObject<ReadyActionArguments>(argumentsJson); } catch { }
        if (args == null)
            return new ToolOutcome("预行动失败：参数无法解析，需要 {\"action\":\"add\",\"tool_name\":\"use_prop\",\"ready\":[...]}。");

        string action = string.IsNullOrWhiteSpace(args.action) ? "add" : args.action.Trim().ToLowerInvariant();
        switch (action)
        {
            case "list":
                return new ToolOutcome("预行动清单：\n" + ReadyActionManager.ListText(callStage));
            case "remove":
                return new ToolOutcome(ReadyActionManager.Remove(callStage, args.id));
            case "clear":
                return new ToolOutcome(ReadyActionManager.Clear(callStage));
            case "pause":
                return new ToolOutcome(ReadyActionManager.SetPaused(callStage, args.id, true));
            case "resume":
                return new ToolOutcome(ReadyActionManager.SetPaused(callStage, args.id, false));
            default:
            {
                string msg = ReadyActionManager.Add(callStage, args.tool_name, args.ready, args.reuse);
                if (msg.StartsWith("登记预行动失败")) return new ToolOutcome(msg);
                return new ToolOutcome(msg, "【预行动】已登记");
            }
        }
    }

    /// <summary>
    /// 预行动系统专用：按工具名 + JSON 参数**直接执行一次**（不走 AI 请求、不附视野截图、不飘字）。
    /// 返回 false 表示没做成，error 里是失败原因（会被预行动系统合并计数后在情报里显示）。
    /// </summary>
    public bool ExecuteToolDirect(string toolName, string argumentsJson, int callStage, out string error)
    {
        error = null;
        ToolOutcome outcome;

        switch (toolName)
        {
            case UsePropToolName: outcome = UseProp(argumentsJson, callStage); break;
            case MergePropToolName: outcome = MergeProp(argumentsJson, callStage); break;
            case ControlTurretToolName: outcome = ControlTurret(argumentsJson, callStage); break;
            case MoveTurretToolName:
            {
                bool previewCreated = false;
                outcome = MoveTurret(argumentsJson, callStage, out previewCreated);
                // 预行动给的是"一步到位"的参数（方向 + 距离）：这次预览成功了就顺手补上 confirm，
                // 否则它只会留下一个待确认的预览、炮塔根本不动。
                if (previewCreated && !string.IsNullOrEmpty(outcome.action))
                    outcome = MoveTurret("{\"confirm\":true}", callStage, out _);
                break;
            }
            case WhisperToolName:
                _ = Whisper(argumentsJson, callStage);   // 悄悄话要等对方回话，异步发出去就算成功
                return true;
            default:
                error = "未知工具：" + toolName;
                return false;
        }

        if (string.IsNullOrEmpty(outcome.action))
        {
            error = outcome.result;      // 失败时 action 为空、result 就是失败原因
            return false;
        }
        return true;
    }

    private ToolOutcome UseProp(string argumentsJson, int callStage)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return new ToolOutcome("使用道具失败：游戏配置(MapConfig)不存在。");

        if (callStage < 0 || callStage >= config.teamProps.Length)
            return new ToolOutcome($"使用道具失败：当前角色阵营 {callStage} 无效。");

        List<PropEntry> props = config.teamProps[callStage];
        if (props == null)
            return new ToolOutcome($"使用道具失败：{callStage} 号阵营没有道具栏。");

        if (!TryParseArguments(argumentsJson, out UsePropArguments args))
            return new ToolOutcome("使用道具失败：参数错误，至少需要 {\"index\": 从1开始的槽位序号}。");

        if (args.index < 1 || args.index > props.Count)
            return new ToolOutcome($"使用道具失败：当前只有 {props.Count} 个道具，无法使用第 {args.index} 格。");

        PropEntry prop = props[args.index - 1];

        // 先解析朝向，失败时不消耗道具。
        ItemType aim = ResolveAim(args.target_guid, args.aim_x, args.aim_y, out string aimError);
        if (!string.IsNullOrWhiteSpace(args.target_guid) && aim == null)
            return new ToolOutcome($"使用道具失败：{aimError}。");
        if (string.IsNullOrWhiteSpace(args.target_guid) && (args.aim_x.HasValue != args.aim_y.HasValue))
            return new ToolOutcome("使用道具失败：aim_x 和 aim_y 必须同时提供。");

        // 【任意】专属：AI 可以从全部四种武器里指定实际触发的武器。
        WeaponKind? anyChoice = null;
        if (prop.item == WeaponKind.任意)
        {
            if (!string.IsNullOrWhiteSpace(args.weapon))
            {
                if (!TryParseWeapon(args.weapon, out WeaponKind choice))
                    return new ToolOutcome($"使用道具失败：weapon 参数无效：{args.weapon}。可选值：{string.Join("、", MapConfig.ConcreteWeaponNames())}。");
                anyChoice = choice;
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.weapon))
        {
            return new ToolOutcome("使用道具失败：只有【任意】道具才能使用 weapon 参数指定武器。");
        }

        // 先取出并移除，再走原有执行逻辑（和 AddProp 里溢出执行的是同一套逻辑）。
        props.RemoveAt(args.index - 1);

        // AI 调用道具的初始瞄准误差：基础 BaseAimAngleError，每级炮塔升级缩小到 1/AimErrorShrinkPerLevel，可叠加。
        float aimAngleError = 0f;
        if (aim != null && aim.item != null)
        {
            int upgradeLevel = 0;
            if (Towel.AllTowel.TryGetValue(callStage, out Towel aimTowel) && aimTowel != null)
                upgradeLevel = Mathf.Max(0, aimTowel.turretUpgraded);
            aimAngleError = BaseAimAngleError / Mathf.Pow(AimErrorShrinkPerLevel, upgradeLevel);
        }


        WeaponKind executed = config.ExecutePropEffect(prop.stage, prop.item, prop.value, aim, anyChoice, aimAngleError);

        string choiceText = anyChoice.HasValue ? $"，任意选择为：{anyChoice.Value}" : "";
        string aimText = aim != null ? $"，已朝向 {aim.pos}" : "";
        string result = $"已使用第 {args.index} 格道具：{prop.item}，数值 {prop.value.ToShortString()}{choiceText}{aimText}。";

        // 玩家侧的具体行动描述：什么道具、多少数值、**实际打出去的武器**、瞄准谁。
        // 【任意】没指定武器时也要把随机到的那个写出来 —— 只写一句「随机武器」观众看不出来它干了什么。
        string actionChoice = prop.item == WeaponKind.任意
            ? $"\n{(anyChoice.HasValue ? "选择 " : "")}{executed}"
            : "";
        string actionAim = aim != null ? $"\n瞄准 {DescribeAimTarget(aim)}" : "";
        string action = $"【{prop.item}】{prop.value.ToShortString()}{actionChoice}{actionAim}";

        return new ToolOutcome(result, action);
    }

    /// <summary>
    /// 朝向解析：
    /// 1. 优先 target_guid：直接用 InformGetter 注册过的场上实体。
    /// 2. 其次 aim_x/aim_y：移动一个无渲染的隐藏锚点，让 AimController 持续锁定该点。
    /// 失败时 error 说明具体原因（目标已被击杀 / guid 不存在 / 没给目标），供工具文案直接回给 AI。
    /// </summary>
    private ItemType ResolveAim(string targetGuid, double? aimX, double? aimY, out string error)
    {
        error = null;
        string guidMissReason = null;

        if (!string.IsNullOrWhiteSpace(targetGuid))
        {
            string guid = targetGuid.Trim();
            if (InformGetter.GuidToTransform.TryGetValue(guid, out Transform target) && target != null)
            {
                IStageValue targetSv = target.GetComponent<IStageValue>();
                if (targetSv != null && WhisperManager.IsDead(targetSv.stage))
                {
                    error = $"{targetSv.stage}号阵营({AIAgent.GetStageName(targetSv.stage)}) 已被击杀，这个 guid 已失效";
                    return null;//目标角色已被击杀，禁止工具指向它
                }
                return new ItemType(target, "AI瞄准目标");
            }
            // guid 失效时继续尝试坐标瞄准，但记下原因
            guidMissReason = $"找不到目标 guid：{guid}（目标可能已被摧毁或已过期）";
        }

        if (aimX.HasValue && aimY.HasValue)
        {
            if (aimAnchor == null)
            {
                GameObject anchorGo = new GameObject("AI_AimAnchor");
                aimAnchor = anchorGo.transform;
            }
            aimAnchor.position = new Vector3((float)aimX.Value, (float)aimY.Value, 0f);
            return new ItemType(aimAnchor, "AI瞄准点");
        }

        error = guidMissReason ?? "没有给出瞄准目标（target_guid 或 aim_x+aim_y）";
        return null;
    }

    /// <summary>
    /// 控制自己炮塔的持续瞄准：
    /// action=start 时必须带 target_guid 或 aim_x+aim_y；action=stop 时交还自动旋转。
    /// </summary>
    private ToolOutcome ControlTurret(string argumentsJson, int callStage)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return new ToolOutcome("炮塔控制失败：游戏配置(MapConfig)不存在。");

        if (!TryParseTurretArguments(argumentsJson, out TurretControlArguments args))
            return new ToolOutcome("炮塔控制失败：参数错误，至少需要 {\"action\": \"start\" 或 \"stop\"}。");

        if (!Towel.AllTowel.TryGetValue(callStage, out Towel towel) || towel == null)
            return new ToolOutcome($"炮塔控制失败：{callStage} 号阵营的炮塔不存在。");

        AimController aimController = towel.aimController;
        if (aimController == null)
            return new ToolOutcome($"炮塔控制失败：{callStage} 号阵营没有挂 AimController。");

        string action = (args.action ?? "").Trim().ToLower();
        if (action == "stop")
        {
            aimController.StopControl();
            return new ToolOutcome("已停止炮塔控制，炮塔恢复自动旋转自动防御。", "停止炮塔控制");
        }
        if (action != "start")
            return new ToolOutcome($"炮塔控制失败：action 无效：{args.action}。可选值：start、stop。");

        ItemType aim = ResolveAim(args.target_guid, args.aim_x, args.aim_y, out string aimError);
        if (aim == null)
        {
            if (args.aim_x.HasValue != args.aim_y.HasValue)
                return new ToolOutcome("炮塔控制失败：aim_x 和 aim_y 必须同时提供。炮塔保持自动旋转自动防御。");
            return new ToolOutcome($"炮塔控制失败：{aimError}。炮塔保持自动旋转自动防御。");
        }

        // 瞄不了就别接管：超过 4 格 AimController 会立刻断开，那还不如不接管、保持自动防御。
        float distance = Vector2.Distance(aim.pos, towel.transform.position);
        if (distance > aimController.MaxDis)
            return new ToolOutcome(
                $"炮塔控制失败：目标距离 {distance:0.00} 超过炮塔最大跟踪距离 {aimController.MaxDis:0.00}，无法瞄准；"
                + "未接管炮塔，它保持自动旋转自动防御。想打远处请用大球、霰弹这类有指向性的道具。");

        if (!aimController.StartControl(aim))
            return new ToolOutcome("炮塔控制失败：瞄准目标无效。炮塔保持自动旋转自动防御。");

        return new ToolOutcome(
            $"炮塔开始持续瞄准 {aim.pos}（距离 {distance:0.00}，最多持续12秒；超时、目标离开 4 格或目标消失会自动恢复自动旋转）。",
            $"炮塔持续瞄准\n{DescribeAimTarget(aim)}");
    }

    /// <summary>
    /// 把“方向+距离”换算成移动预览：归一化方向、按最大移动距离夹距离、算目标点与警告。
    /// 纯计算，不碰任何游戏对象，便于单独验证。
    /// </summary>
    public static bool ResolveMovePreview(Vector2 selfPos, Vector2 dir, float distance, float maxMoveDistance, float moveBound,
        out Vector2 target, out float travelDistance, out string warning, out string error)
    {
        target = selfPos;
        travelDistance = 0f;
        warning = "";
        error = "";

        if (dir.sqrMagnitude < 0.0000001f)
        {
            error = "移动炮塔失败：方向无效（向量的长度不能为 0）。";
            return false;
        }
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f)
        {
            error = "移动炮塔失败：distance 必须是 >= 0 的数字。";
            return false;
        }

        Vector2 n = dir.normalized;
        travelDistance = Mathf.Clamp(distance, 0f, Mathf.Max(0f, maxMoveDistance));
        target = selfPos + n * travelDistance;

        if (distance > maxMoveDistance + 0.0001f)
            warning = "距离超出当前最大移动距离 " + maxMoveDistance.ToString("0.00") + "，实际只会移动 " + travelDistance.ToString("0.00") + "。";

        if (Mathf.Abs(target.x) > moveBound + 0.0001f || Mathf.Abs(target.y) > moveBound + 0.0001f)
        {
            if (warning.Length > 0) warning += " ";
            warning += "目标点 (" + target.x.ToString("0.00") + ", " + target.y.ToString("0.00")
                + ") 超出可移动范围（|x|,|y| ≤ " + moveBound.ToString("0.00")
                + "）：实际移动会在碰到地图边界时撞墙停下，走不到这个点。";
        }

        return true;
    }

    /// <summary>移动自己的炮塔：第一次只预览（不移动），第二次 confirm=true 才执行。
    /// previewCreated = true 表示这次确实产出了一份新预览（调用方据此附上炮塔视野截图）。</summary>
    private ToolOutcome MoveTurret(string argumentsJson, int callStage, out bool previewCreated)
    {
        previewCreated = false;

        if (!TryParseMoveTurretArguments(argumentsJson, out MoveTurretArguments args))
            return new ToolOutcome("移动炮塔失败：参数无法解析，需要方向（angle 或 dir_x+dir_y）与 distance，或 confirm=true。");

        if (!Towel.AllTowel.TryGetValue(callStage, out Towel towel) || towel == null)
            return new ToolOutcome($"移动炮塔失败：找不到 {callStage} 号阵营的炮塔。");
        if (towel.isDead)
            return new ToolOutcome("移动炮塔失败：炮塔已阵亡。");

        // 能量检查放在**所有分支的最前面**：这一步决定要不要产出预览，
        // 而预览会附带一张视野截图（渲染 + 读回 + 编码，是最贵的一步）。
        // 放在最后查的话，参数解析、目标点计算都白做，而且一旦以后有人在前面
        // 把 previewCreated 置起来，就会变成"没能量也一直截图"。
        MarbleManager mm = MarbleManager.Instance;
        float cost = mm != null
            ? mm.GetActionEnergyCost(callStage, MarbleManager.EnergyAction.Move)
            : (MapConfig.Instance != null ? MapConfig.Instance.moveEnergyCost : 0f);
        int moveUsed = mm != null ? mm.GetActionUseCount(callStage, MarbleManager.EnergyAction.Move) : 0;
        float energyNow = mm != null ? mm.GetUpgradeEnergy(callStage) : 0f;
        bool affordable = mm == null || energyNow >= cost;
        string noEnergyText = "移动炮塔失败：升级能量不足（本次移动需要 " + cost.ToString("0.#")
            + (moveUsed > 0 ? "（已经移过 " + moveUsed + " 次，每用一次涨价）" : "")
            + "，当前 " + energyNow.ToString("0.#") + "），所以这次连预览都不给（预览会附带一张视野截图，很贵）。"
            + "能量来自空槽升级进度、会随时间积累；攒够之前不要反复调 move_turret，先做别的（开火/升级/外交），过几轮再试。";

        // 第二步：确认执行（不再需要方向与距离）
        if (args.confirm == true)
        {
            if (!pendingMoves.TryGetValue(callStage, out MovePreview preview) || preview == null
                || Time.time - preview.time > MovePreviewValidSeconds)
            {
                pendingMoves.Remove(callStage);
                return new ToolOutcome("移动炮塔失败：没有待确认的移动预览（或者已超过 60 秒过期）。请先给方向和距离做一次预览，再传 confirm=true。");
            }

            // 移动途中不许改道（这条机制已经去掉）：正在移动时只接受「原地停下」（distance=0）那一种确认
            if (towel.IsMoving && preview.distance > 0f)
            {
                pendingMoves.Remove(callStage);
                return new ToolOutcome(MovingNoRedirectText);
            }

            // 扣除能量（按现价），然后才开始移动
            if (mm != null && !mm.TrySpendActionEnergy(callStage, MarbleManager.EnergyAction.Move, out cost))
            {
                pendingMoves.Remove(callStage);
                return new ToolOutcome(noEnergyText);
            }

            pendingMoves.Remove(callStage);
            Vector2 from = towel.transform.position;
            towel.StartMove(preview.dir, preview.distance);
            float eta = preview.distance / Mathf.Max(towel.moveSpeed, 0.0001f);
            string dirText = InformGetter.CardinalDirection(preview.dir);
            string energyText = mm != null
                ? "已扣除 " + cost.ToString("0.#") + " 能量（剩余 " + mm.GetUpgradeEnergy(callStage).ToString("0.#") + "）。"
                : "";
            if (preview.distance <= 0f)
                return new ToolOutcome("炮塔已停下（原地不动）。" + energyText, "【炮塔停下】");

            return new ToolOutcome(
                "已开始移动：" + towel.MoveStateText + "（从 (" + from.x.ToString("0.00") + ", " + from.y.ToString("0.00")
                + ") 朝 " + dirText + " 移动 " + preview.distance.ToString("0.00") + "，预计 " + eta.ToString("0.0")
                + " 秒；到达、撞到地图边界或超时会通知你）。" + energyText,
                $"【移动炮塔】朝 {dirText} 移动 {preview.distance.ToString("0.00")}");
        }

        // 第一步：只预览，绝不移动。
        // 没能量就别给预览：预览本身会附带一张视野截图，是最贵的一步，白跑一次纯浪费。
        if (!affordable)
            return new ToolOutcome(noEnergyText);

        Vector2 dir;
        if (args.dir_x.HasValue || args.dir_y.HasValue)
        {
            if (!args.dir_x.HasValue || !args.dir_y.HasValue)
                return new ToolOutcome("移动炮塔失败：dir_x 和 dir_y 必须同时提供（或者改用 angle）。");
            dir = new Vector2((float)args.dir_x.Value, (float)args.dir_y.Value);
        }
        else if (args.angle.HasValue)
        {
            float rad = (float)args.angle.Value * Mathf.Deg2Rad;
            dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        }
        else
        {
            return new ToolOutcome("移动炮塔失败：请给出方向——angle（角度，0=地图右、逆时针为正）或 dir_x+dir_y（向量）。");
        }

        if (!args.distance.HasValue)
            return new ToolOutcome("移动炮塔失败：请给出 distance（移动距离，世界单位）。");

        // 移动途中不许改道（这条机制已经去掉）：正在移动时只给「原地停下」（distance=0）的预览
        if (towel.IsMoving && args.distance.Value > 0f)
            return new ToolOutcome(MovingNoRedirectText);

        Vector2 selfPos = towel.transform.position;
        if (!ResolveMovePreview(selfPos, dir, (float)args.distance.Value, towel.MaxMoveDistance, towel.MoveBound,
                out Vector2 target, out float travel, out string warning, out string error))
            return new ToolOutcome(error);

        // 能量已经在函数开头查过（affordable），这里直接复用，别再查一遍
        MarbleManager mmPreview = mm;
        float moveCost = cost;
        float moveEnergy = energyNow;

        pendingMoves[callStage] = new MovePreview { dir = dir.normalized, distance = travel, time = Time.time };
        previewCreated = true;

        Vector2 nd = dir.normalized;
        float angleDeg = Mathf.Repeat(Vector2.SignedAngle(Vector2.right, nd), 360f);
        float etaPreview = travel / Mathf.Max(towel.moveSpeed, 0.0001f);

        var sb = new StringBuilder();
        sb.AppendLine("移动预览（尚未移动）：");
        sb.AppendLine("  当前位置 (" + selfPos.x.ToString("0.00") + ", " + selfPos.y.ToString("0.00") + ")");
        sb.AppendLine("  方向 " + angleDeg.ToString("0") + "°（" + InformGetter.CardinalDirection(nd) + "），要求距离 "
            + ((float)args.distance.Value).ToString("0.00") + "，实际移动 " + travel.ToString("0.00"));
        sb.AppendLine("  目标点 (" + target.x.ToString("0.00") + ", " + target.y.ToString("0.00") + ")，预计 "
            + etaPreview.ToString("0.0") + " 秒（速度 " + towel.moveSpeed.ToString("0.00") + "/秒）");
        sb.AppendLine("  你的最大移动距离 " + towel.MaxMoveDistance.ToString("0.00") + "，可移动范围 x,y ∈ [-"
            + towel.MoveBound.ToString("0.00") + ", " + towel.MoveBound.ToString("0.00") + "]");
        if (mmPreview != null)
            sb.AppendLine("  本次移动消耗 " + moveCost.ToString("0.#") + " 能量（每用一次涨价，这是第 "
                + (moveUsed + 1) + " 次；当前 " + moveEnergy.ToString("0.#") + " → 移动后剩余 "
                + (moveEnergy - moveCost).ToString("0.#") + "）");
        if (!string.IsNullOrEmpty(warning)) sb.AppendLine("  ⚠ " + warning);
        sb.Append("如需执行，请再调用一次 move_turret 并传 confirm=true（不要再传方向与距离）。");

        // 预览不算一次实际动作，不飘字。
        return new ToolOutcome(sb.ToString());
    }

    private static bool TryParseMoveTurretArguments(string argumentsJson, out MoveTurretArguments args)
    {
        args = null;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;

        try
        {
            args = JsonConvert.DeserializeObject<MoveTurretArguments>(argumentsJson);
            return args != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>AI 间悄悄话：交给 WhisperManager 调度，等对方回复后作为 tool result 返回。</summary>
    private async Task<ToolOutcome> Whisper(string argumentsJson, int callStage)
    {
        if (!TryParseWhisperArguments(argumentsJson, out WhisperArguments args))
            return new ToolOutcome("悄悄话失败：参数错误，需要 {\"to\": 对方阵营1-4, \"content\": 内容}。");

        if (args.to < 1 || args.to > 4)
            return new ToolOutcome($"悄悄话失败：to 必须是1-4的阵营编号，收到 {args.to}。");

        string whisperResult = await WhisperManager.WhisperAsync(callStage, args.to, args.content, currentRound);
        string result = whisperResult + "\n\n提醒：以上是悄悄话工具结果，只有你自己可见。你接下来最终生成的 content 是公开发言，所有玩家都会看到；请不要在公开发言里重复悄悄话内容或相关私密信息。";

        // 悄悄话内容和对方回复都属于私密信息：公开飘字只说“给谁发了悄悄话”。
        // WhisperAsync 失败时一律返回“悄悄话失败：…”前缀，那时候没发出去，就不飘字。
        string action = !string.IsNullOrEmpty(whisperResult) && whisperResult.StartsWith("悄悄话失败")
            ? null
            : $"悄悄话 → {AIAgent.GetStageName(args.to)}";

        return new ToolOutcome(result, action);
    }

    private static bool TryParseWhisperArguments(string argumentsJson, out WhisperArguments args)
    {
        args = null;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;

        try
        {
            args = JsonConvert.DeserializeObject<WhisperArguments>(argumentsJson);
            return args != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseTurretArguments(string argumentsJson, out TurretControlArguments args)
    {
        args = null;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;

        try
        {
            args = JsonConvert.DeserializeObject<TurretControlArguments>(argumentsJson);
            return args != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseArguments(string argumentsJson, out UsePropArguments args)
    {
        args = null;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return false;

        try
        {
            args = JsonConvert.DeserializeObject<UsePropArguments>(argumentsJson);
            if (args != null) return true;
        }
        catch
        {
            // 下面再尝试纯数字兜底：只表示 index
        }

        if (int.TryParse(argumentsJson.Trim(), out int index))
        {
            args = new UsePropArguments { index = index };
            return true;
        }

        return false;
    }

    private static bool TryParseWeapon(string text, out WeaponKind weapon)
    {
        weapon = WeaponKind.任意;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 不写死列表：凡是能对上某个"实体武器"名字的就算有效（含穿甲，以及以后新增的武器）
        string t = text.Trim();
        foreach (WeaponKind kind in MapConfig.AllConcreteWeapons)
        {
            if (kind.ToString() == t) { weapon = kind; return true; }
        }
        return false;
    }

    private void OnDestroy()
    {
        if (aimAnchor != null)
            Destroy(aimAnchor.gameObject);
    }

    /// <summary>
    /// 一次工具调用的两个产物：
    /// result 进 role=tool 消息，只给模型看（含失败原因）；
    /// action 飘到炮塔旁给玩家看，写的是“AI 做了什么”，为空表示这次调用没做成、不飘字。
    /// </summary>
    private struct ToolOutcome
    {
        public string result;
        public string action;

        public ToolOutcome(string result, string action = null)
        {
            this.result = result;
            this.action = action;
        }
    }

    [System.Serializable]
    private class ReadyActionArguments
    {
        public string action = "add";
        public string tool_name = "";
        public List<string> ready = new List<string>();
        public int reuse = 0;
        public int id = 0;
    }

    [System.Serializable]
    private class MergePropArguments
    {
        public int index_a = 0;
        public int index_b = 0;
    }

    [System.Serializable]
    private class UsePropArguments
    {
        public int index = 0;
        public string target_guid = "";
        public double? aim_x = null;
        public double? aim_y = null;
        public string weapon = "";
    }

    [System.Serializable]
    private class TurretControlArguments
    {
        public string action = "";
        public string target_guid = "";
        public double? aim_x = null;
        public double? aim_y = null;
    }

    [System.Serializable]
    private class MoveTurretArguments
    {
        public double? angle = null;
        public double? dir_x = null;
        public double? dir_y = null;
        public double? distance = null;
        public bool? confirm = null;
    }

    [System.Serializable]
    private class WhisperArguments
    {
        public int to = 0;
        public string content = "";
    }
}
