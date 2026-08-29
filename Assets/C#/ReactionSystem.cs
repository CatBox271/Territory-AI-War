using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 行动系统：把游戏内的可用行动包装成 AI 可调用的 Function Tools。
/// 目前只开放一个工具：use_prop（使用自己阵营武器栏里的道具，并可选朝向）。
/// 所有调用都只走 MapConfig.ExecutePropEffect，不新增任何游戏性逻辑。
/// </summary>
public class ReactionSystem : MonoBehaviour, Itool
{
    [Tooltip("当前调用工具的角色阵营（由 AIAgent 在请求前写入 CharacterCard.position）")]
    public int stage = -1;

    public List<Tool> tools = new List<Tool>
    {
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = UsePropToolName,
                description = "使用自己阵营武器栏中指定格子的道具。index 从 1 开始：1=第 1 格，2=第 2 格，以此类推。需要朝目标射击时，优先传 target_guid（场上信息里的 guid）；没有 guid 时传 aim_x 和 aim_y 指定地图世界坐标。使用成功后道具立即消耗并生效。没有道具或 index 超过当前持有数量时不要调用。如果该道具是【任意】，可以传 weapon 从霰弹、扫射、护盾、大球里指定实际触发的武器；不传则随机。",
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
        }
    };

    private const string UsePropToolName = "use_prop";

    // 坐标瞄准用的隐藏锚点：不创建实体，只提供一个 Transform 给既有 AimController 持续跟踪。
    private Transform aimAnchor;


    /// <summary>
    /// Itool 实现：AI 每发起一轮 tool_calls，AIRequest 会把这些调用传进来。
    /// 每个 ToolCall 必须返回一条 role=tool 的消息，并带原 tool_call_id。
    /// </summary>
    public Task<List<DeepSeekMessage>> DealToolCallsAsync(List<ToolCall> toolCalls)
    {
        List<DeepSeekMessage> result = new List<DeepSeekMessage>();
        if (toolCalls == null) return Task.FromResult(result);

        foreach (ToolCall call in toolCalls)
        {
            result.Add(DealToolCall(call));
        }

        return Task.FromResult(result);
    }

    private DeepSeekMessage DealToolCall(ToolCall call)
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
            content = UseProp(call.function.arguments);
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
    private string UseProp(string argumentsJson)
    {
        MapConfig config = MapConfig.Instance;
        if (config == null)
            return "使用道具失败：游戏配置(MapConfig)不存在。";

        if (stage < 0 || stage >= config.teamProps.Length)
            return $"使用道具失败：当前角色阵营 {stage} 无效。";

        List<PropEntry> props = config.teamProps[stage];
        if (props == null)
            return $"使用道具失败：{stage} 号阵营没有道具栏。";

        if (!TryParseArguments(argumentsJson, out UsePropArguments args))
            return "使用道具失败：参数错误，至少需要 {\"index\": 从1开始的槽位序号}。";

        if (args.index < 1 || args.index > props.Count)
            return $"使用道具失败：当前只有 {props.Count} 个道具，无法使用第 {args.index} 格。";

        PropEntry prop = props[args.index - 1];

        // 先解析朝向，失败时不消耗道具。
        ItemType aim = ResolveAim(args);
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
    private ItemType ResolveAim(UsePropArguments args)
    {
        if (!string.IsNullOrWhiteSpace(args.target_guid))
        {
            if (InformGetter.GuidToTransform.TryGetValue(args.target_guid.Trim(), out Transform target) && target != null)
                return new ItemType(target, "AI瞄准目标");
            // guid 失效时继续尝试坐标瞄准
        }

        if (args.aim_x.HasValue && args.aim_y.HasValue)
        {
            if (aimAnchor == null)
            {
                GameObject anchorGo = new GameObject("AI_AimAnchor");
                aimAnchor = anchorGo.transform;
            }
            aimAnchor.position = new Vector3((float)args.aim_x.Value, (float)args.aim_y.Value, 0f);
            return new ItemType(aimAnchor, "AI瞄准点");
        }

        return null;
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
}
