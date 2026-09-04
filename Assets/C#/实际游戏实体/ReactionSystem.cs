using System.Collections.Generic;
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

    [System.NonSerialized] // 工具表以代码内联定义为准，不进场景序列化（object 参数无法被 Unity 序列化）
    public List<Tool> tools = new List<Tool>
    {
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = UsePropToolName,
                description = "使用自己阵营武器栏中指定格子的道具。index 从 1 开始：1=第 1 格，2=第 2 格，以此类推。需要朝目标射击时，优先传 target_guid（场上信息里的 guid）；没有 guid 时传 aim_x 和 aim_y 指定地图世界坐标。使用成功后道具立即消耗并生效。没有道具或 index 超过当前持有数量时不要调用。如果该道具是【任意】，可以传 weapon 从霰弹、扫射、护盾、大球里指定实际触发的武器；不传则随机。一次使用多个道具时，请按武器栈从后往前（高 index  低 index）依次调用，减少槽位反复移动。",
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
                                @enum = new List<string> { "霰弹", "扫射", "护盾", "大球" },
                                description = "仅当目标道具是【任意】时生效：指定实际要触发的武器，可选值：霰弹、扫射、护盾、大球。其他道具不要传。"
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
                name = WhisperToolName,
                  description = "给另一个AI发送悄悄话并等待对方回复。to=对方阵营编号(1-4，不能是自己)；content=悄悄话内容，请用对方的名字称呼对方（玩家名单见系统提示）。对方空闲时立即回复；对方忙碌时会等对方忙完再单独回复。每4回合只能使用一次。如果出现互相等待或环形等待，系统会自动调配，返回结果里会说明。",
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
        }
    };

    private const string UsePropToolName = "use_prop";
    private const string ControlTurretToolName = "control_turret";
    private const string WhisperToolName = "whisper";

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
        foreach (ToolCall call in toolCalls)
        {
            result.Add(await DealToolCall(call, callStage));
        }

        return result;
    }

    private async Task<DeepSeekMessage> DealToolCall(ToolCall call, int callStage)
    {
        string content;

        if (call == null)
        {
            content = "工具调用失败：ToolCall 为空。";
        }
        else if (call.function == null || string.IsNullOrEmpty(call.function.name))
        {
            content = "工具调用失败：缺少 function.name。";
        }
        else if (call.function.name == UsePropToolName)
        {
            content = UseProp(call.function.arguments, callStage);
        }
        else if (call.function.name == ControlTurretToolName)
        {
            content = ControlTurret(call.function.arguments, callStage);
        }
        else if (call.function.name == WhisperToolName)
        {
            content = await Whisper(call.function.arguments, callStage);
        }
        else
        {
            content = $"未知工具：{call.function.name}";
        }

        return new DeepSeekMessage
        {
            role = "tool",
            content = content,
            tool_call_id = call != null ? call.id : ""
        };
    }


    /// <summary>使用道具：消耗指定槽位，并连同瞄准目标交给既有游戏逻辑 ExecutePropEffect 执行。</summary>
    private string UseProp(string argumentsJson, int callStage)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return "使用道具失败：游戏配置(MapConfig)不存在。";

        if (callStage < 0 || callStage >= config.teamProps.Length)
            return $"使用道具失败：当前角色阵营 {callStage} 无效。";

        List<PropEntry> props = config.teamProps[callStage];
        if (props == null)
            return $"使用道具失败：{callStage} 号阵营没有道具栏。";

        if (!TryParseArguments(argumentsJson, out UsePropArguments args))
            return "使用道具失败：参数错误，至少需要 {\"index\": 从1开始的槽位序号}。";

        if (args.index < 1 || args.index > props.Count)
            return $"使用道具失败：当前只有 {props.Count} 个道具，无法使用第 {args.index} 格。";

        PropEntry prop = props[args.index - 1];

        // 先解析朝向，失败时不消耗道具。
        ItemType aim = ResolveAim(args.target_guid, args.aim_x, args.aim_y);
        if (!string.IsNullOrWhiteSpace(args.target_guid) && aim == null)
            return $"使用道具失败：找不到目标 guid：{args.target_guid}。";
        if (string.IsNullOrWhiteSpace(args.target_guid) && (args.aim_x.HasValue != args.aim_y.HasValue))
            return "使用道具失败：aim_x 和 aim_y 必须同时提供。";

        // 【任意】专属：AI 可以从全部四种武器里指定实际触发的武器。
        WeaponKind? anyChoice = null;
        if (prop.item == WeaponKind.任意)
        {
            if (!string.IsNullOrWhiteSpace(args.weapon))
            {
                if (!TryParseWeapon(args.weapon, out WeaponKind choice))
                    return $"使用道具失败：weapon 参数无效：{args.weapon}。可选值：霰弹、扫射、护盾、大球。";
                anyChoice = choice;
            }
        }
        else if (!string.IsNullOrWhiteSpace(args.weapon))
        {
            return "使用道具失败：只有【任意】道具才能使用 weapon 参数指定武器。";
        }

        // 先取出并移除，再走原有执行逻辑（和 AddProp 里溢出执行的是同一套逻辑）。
        props.RemoveAt(args.index - 1);

        config.ExecutePropEffect(prop.stage, prop.item, prop.value, aim, anyChoice);

        string choiceText = anyChoice.HasValue ? $"，任意触发为：{anyChoice.Value}" : "";
        string aimText = aim != null ? $"，已朝向 {aim.pos}" : "";
        return $"已使用第 {args.index} 格道具：{prop.item}，数值 {prop.value.ToShortString()}{choiceText}{aimText}。";
    }

    /// <summary>
    /// 朝向解析：
    /// 1. 优先 target_guid：直接用 InformGetter 注册过的场上实体。
    /// 2. 其次 aim_x/aim_y：移动一个无渲染的隐藏锚点，让 AimController 持续锁定该点。
    /// </summary>
    private ItemType ResolveAim(string targetGuid, double? aimX, double? aimY)
    {
        if (!string.IsNullOrWhiteSpace(targetGuid))
        {
            if (InformGetter.GuidToTransform.TryGetValue(targetGuid.Trim(), out Transform target) && target != null)
            {
                IStageValue targetSv = target.GetComponent<IStageValue>();
                if (targetSv != null && WhisperManager.IsDead(targetSv.stage))
                    return null;//目标角色已被击杀，禁止工具指向它
                return new ItemType(target, "AI瞄准目标");
            }
            // guid 失效时继续尝试坐标瞄准
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

        return null;
    }

    /// <summary>
    /// 控制自己炮塔的持续瞄准：
    /// action=start 时必须带 target_guid 或 aim_x+aim_y；action=stop 时交还自动旋转。
    /// </summary>
    private string ControlTurret(string argumentsJson, int callStage)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return "炮塔控制失败：游戏配置(MapConfig)不存在。";

        if (!TryParseTurretArguments(argumentsJson, out TurretControlArguments args))
            return "炮塔控制失败：参数错误，至少需要 {\"action\": \"start\" 或 \"stop\"}。";

        if (!Towel.AllTowel.TryGetValue(callStage, out Towel towel) || towel == null)
            return $"炮塔控制失败：{callStage} 号阵营的炮塔不存在。";

        AimController aimController = towel.aimController;
        if (aimController == null)
            return $"炮塔控制失败：{callStage} 号阵营没有挂 AimController。";

        string action = (args.action ?? "").Trim().ToLower();
        if (action == "stop")
        {
            aimController.StopControl();
            return "已停止炮塔控制，炮塔恢复自动旋转。";
        }
        if (action != "start")
            return $"炮塔控制失败：action 无效：{args.action}。可选值：start、stop。";

        ItemType aim = ResolveAim(args.target_guid, args.aim_x, args.aim_y);
        if (aim == null)
        {
            if (!string.IsNullOrWhiteSpace(args.target_guid))
                return $"炮塔控制失败：找不到目标 guid：{args.target_guid}。";
            if (args.aim_x.HasValue != args.aim_y.HasValue)
                return "炮塔控制失败：aim_x 和 aim_y 必须同时提供。";
            return "炮塔控制失败：start 需要指定瞄准目标（target_guid 或 aim_x+aim_y）。";
        }

        if (!aimController.StartControl(aim))
            return "炮塔控制失败：瞄准目标无效。";

        return $"炮塔开始持续瞄准 {aim.pos}（最多持续12秒，超时或目标消失会自动恢复自动旋转）。";
    }

    /// <summary>AI 间悄悄话：交给 WhisperManager 调度，等对方回复后作为 tool result 返回。</summary>
    private async Task<string> Whisper(string argumentsJson, int callStage)
    {
        if (!TryParseWhisperArguments(argumentsJson, out WhisperArguments args))
            return "悄悄话失败：参数错误，需要 {\"to\": 对方阵营1-4, \"content\": 内容}。";

        if (args.to < 1 || args.to > 4)
            return $"悄悄话失败：to 必须是1-4的阵营编号，收到 {args.to}。";

        string whisperResult = await WhisperManager.WhisperAsync(callStage, args.to, args.content, currentRound);
        return whisperResult + "\n\n提醒：以上是悄悄话工具结果，只有你自己可见。你接下来最终生成的 content 是公开发言，所有玩家都会看到；请不要在公开发言里重复悄悄话内容或相关私密信息。";
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

        string t = text.Trim();
        if (t == "霰弹") weapon = WeaponKind.霰弹;
        else if (t == "扫射") weapon = WeaponKind.扫射;
        else if (t == "护盾") weapon = WeaponKind.护盾;
        else if (t == "大球") weapon = WeaponKind.大球;
        else return false;
        return true;
    }

    private void OnDestroy()
    {
        if (aimAnchor != null)
            Destroy(aimAnchor.gameObject);
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
    private class WhisperArguments
    {
        public int to = 0;
        public string content = "";
    }
}
