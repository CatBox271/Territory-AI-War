using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// 预行动系统：AI 可以登记"等条件满足再替我执行"的工具调用。
///
/// 用法（只用这一个专用工具 <see cref="ToolName"/>）：
///   ready_action { action:"add", tool_name:"use_prop", reuse:0,
///                  ready:["all--left--less--1", "all--prop--contain--大球--value--less--1048576",
///                         "select--prop--contain--大球--value--less--1048576", "para--aim_x--0", "para--aim_y--0"] }
/// 语义：道具槽满了、且栈里有一个 value &lt; 1048576 的大球 → 自动挑它朝 (0,0) 打出去腾空槽。
///
/// 规则（已和需求方定死的口径）：
///   · 每帧轮询；允许与 AI 本轮的普通行动**同时**发生；
///   · 一个阵营最多 <see cref="MaxPerStage"/> 条（= 10，够把清槽 / 补盾 / 挪窝整套自动化）；
///     `reuse`：0=执行一次后自动取消（默认）、-1=永久、N=存活 N 个 AI 轮次；
///   · 触发失败（能量不足/参数非法/格号越界…）→ 这次不执行、这条保留，
///     失败**按原因合并计数**（防止刷屏），每回合在情报里显示；
///   · 触发成功**不给玩家侧飘自定义文字**（执行本身在场上看得见，不给对手额外情报）。
/// </summary>
public static class ReadyActionManager
{
    public const string ToolName = "ready_action";
    public const int MaxPerStage = 10;

    /// <summary>一条预行动。</summary>
    public class Action
    {
        public int id;
        public int stage;
        public string toolName = "";
        public List<string> raw = new();          // 原始 ready 串（含条件 / select / para / reuse）
        public int reuse;                         // 0=一次后取消 / -1=永久 / N=存活 N 回合
        public bool paused;
        public int bornRound;
        public int executed;
        public readonly Dictionary<string, int> fails = new();   // 失败原因 -> 次数（合并防刷屏）
        public string lastFail = "";
        public string lastFailRound = "";
    }

    private static readonly Dictionary<int, List<Action>> byStage = new();
    private static int nextId = 1;

    // ---- 每轮刷新一次的缓存（由 AIAgent / InformGetter 填；避免每帧扫全图）----
    private static readonly Dictionary<int, int> roundOf = new();          // AI 轮次
    private static readonly Dictionary<int, int> marbleCount = new();      // 己方弹珠数
    private static readonly Dictionary<int, long> territory = new();       // 己方领土像素
    private static readonly Dictionary<int, Vector2> threatPos = new();    // 最近敌方领土坐标
    private static readonly Dictionary<int, float> threatDist = new();     // 最近敌方领土距离
    private static readonly Dictionary<int, int> warnShellCount = new();   // 朝我来的穿甲弹数
    private static readonly Dictionary<int, float> warnShellEta = new();   // 其中最近的一发还有几秒
    private static readonly Dictionary<int, Vector2> warnShellFrom = new();// 最近那一发**相对我的方位向量**
    private static readonly Dictionary<int, Vector2> warnShellVel = new(); // 最近那一发的速度（航线方向）
    private static readonly Dictionary<int, int> warnBallCount = new();    // 朝我来的大球数
    private static readonly Dictionary<int, float> warnBallEta = new();

    public static void SetRound(int stage, int round) { roundOf[stage] = round; }
    public static void SetMarbleCount(int stage, int count) { marbleCount[stage] = count; }
    public static void SetTerritory(int stage, long pixels) { territory[stage] = pixels; }

    public static void SetThreat(int stage, Vector2 pos, float dist)
    {
        threatPos[stage] = pos;
        threatDist[stage] = dist;
    }

    public static void SetWarnings(int stage, int shellCount, float shellEta, int ballCount, float ballEta,
        Vector2 shellFrom = default, Vector2 shellVel = default)
    {
        warnShellCount[stage] = shellCount;
        warnShellEta[stage] = shellEta;
        warnShellFrom[stage] = shellFrom;
        warnShellVel[stage] = shellVel;
        warnBallCount[stage] = ballCount;
        warnBallEta[stage] = ballEta;
    }

    /// <summary>
    /// 「垂直于最近那发穿甲弹的航线」的逃生角度（度）：给预行动的 `para--angle--垂直` 用。
    /// 预行动没法自己看那一发从哪来 —— 没有这个现算，它只能拿一个固定角度硬走，
    /// 顺着航线走就等于把自己摆到弹道上（用户反馈："AI 自己向穿甲上送"）。
    /// 取不到来袭信息时返回 null（调用方就别触发这次移动）。
    /// </summary>
    public static float? PerpendicularEscapeAngle(int stage)
    {
        if (!warnShellVel.TryGetValue(stage, out Vector2 v) || v.sqrMagnitude < 0.000001f) return null;
        if (!warnShellFrom.TryGetValue(stage, out Vector2 fromMyPos)) return null;

        // 航线方向 = 弹速方向；垂直于它的两个方向里，挑"离这一发现在的位置更远"的那边
        Vector2 perp = new Vector2(-v.y, v.x).normalized;
        if (Vector2.Dot(perp, -fromMyPos) < 0f) perp = -perp;
        return Mathf.Atan2(perp.y, perp.x) * Mathf.Rad2Deg;
    }

    public static int RoundOf(int stage) => roundOf.TryGetValue(stage, out int r) ? r : 0;

    /// <summary>新一局：清空全部预行动与缓存。</summary>
    public static void ResetAll()
    {
        byStage.Clear();
        roundOf.Clear(); marbleCount.Clear(); territory.Clear();
        threatPos.Clear(); threatDist.Clear();
        warnShellCount.Clear(); warnShellEta.Clear(); warnShellFrom.Clear(); warnShellVel.Clear();
        warnBallCount.Clear(); warnBallEta.Clear();
        nextId = 1;
    }

    // ==================== 登记 / 管理 ====================

    public static string Add(int stage, string toolName, List<string> ready, int reuse)
    {
        if (stage < 0) return "登记预行动失败：阵营无效。";
        if (string.IsNullOrWhiteSpace(toolName)) return "登记预行动失败：缺少 tool_name（要触发的工具名）。";
        if (!IsKnownTool(toolName)) return $"登记预行动失败：tool_name「{toolName}」不是可用的工具。";
        if (ready == null || ready.Count == 0) return "登记预行动失败：ready 至少要有一条条件（如 all--upgrade--more--30）。";

        if (!byStage.TryGetValue(stage, out List<Action> list))
        {
            list = new List<Action>();
            byStage[stage] = list;
        }
        if (list.Count >= MaxPerStage)
            return $"登记预行动失败：最多只能挂 {MaxPerStage} 条，现在已经有 {list.Count} 条——先用 action=remove 删掉不要的。";

        var a = new Action
        {
            id = nextId++,
            stage = stage,
            toolName = toolName,
            raw = new List<string>(ready),
            reuse = reuse,
            bornRound = RoundOf(stage),
        };

        // reuse 也允许写成 ready 里的一条：reuse--5 / reuse---1
        if (reuse == 0)
        {
            foreach (string s in a.raw)
            {
                if (!s.StartsWith("reuse--")) continue;
                string v = s.Substring("reuse--".Length);
                if (int.TryParse(v, out int r)) a.reuse = r;
                break;
            }
        }

        string parseError = ParseCheck(a);
        if (parseError != null) return "登记预行动失败：" + parseError;

        list.Add(a);
        return $"已登记预行动 id{a.id}：触发时调用 {a.toolName}；条件 {DescribeConditions(a)}；"
             + ReuseText(a.reuse) + $"。当前共 {list.Count}/{MaxPerStage} 条。";
    }

    public static string Remove(int stage, int id)
    {
        if (!byStage.TryGetValue(stage, out List<Action> list)) return "删除失败：这个阵营没有预行动。";
        int idx = list.FindIndex(x => x.id == id);
        if (idx < 0) return $"删除失败：没有 id{id} 这条预行动。";
        list.RemoveAt(idx);
        return $"已删除预行动 id{id}，剩 {list.Count} 条。";
    }

    public static string Clear(int stage)
    {
        if (!byStage.TryGetValue(stage, out List<Action> list) || list.Count == 0) return "清空失败：这个阵营没有预行动。";
        int n = list.Count;
        list.Clear();
        return $"已清空 {n} 条预行动。";
    }

    public static string SetPaused(int stage, int id, bool paused)
    {
        if (!byStage.TryGetValue(stage, out List<Action> list)) return "操作失败：这个阵营没有预行动。";
        Action a = list.Find(x => x.id == id);
        if (a == null) return $"操作失败：没有 id{id} 这条预行动。";
        a.paused = paused;
        return $"预行动 id{id} 已{(paused ? "暂停" : "恢复")}。";
    }

    // ==================== 每帧轮询 ====================

    /// <summary>每帧调用：到期清理 → 条件评估 → 触发执行。</summary>
    public static void Tick()
    {
        if (byStage.Count == 0) return;

        foreach (var kv in byStage)
        {
            int stage = kv.Key;
            List<Action> list = kv.Value;
            if (list.Count == 0) continue;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                Action a = list[i];
                if (a == null) { list.RemoveAt(i); continue; }

                // 到期（reuse > 0）：存活 N 个 AI 轮次
                if (a.reuse > 0 && RoundOf(stage) - a.bornRound >= a.reuse)
                {
                    list.RemoveAt(i);
                    continue;
                }

                if (a.paused) continue;

                string parseError = ParseCheck(a);
                if (parseError != null) { RecordFail(a, "条件写法有问题：" + parseError); continue; }

                if (!ConditionsMet(a, out string current)) continue;

                // 触发
                if (TryFire(a, out string error))
                {
                    a.executed++;
                    if (a.reuse == 0) list.RemoveAt(i);   // 默认：执行一次后自动取消
                }
                else
                {
                    RecordFail(a, error);
                }
            }
        }
    }

    private static void RecordFail(Action a, string reason)
    {
        if (string.IsNullOrEmpty(reason)) reason = "未知原因";
        // 合并：同一个原因只加计数，并把最后一次的原因文本留下（防止情报被刷屏）
        a.fails.TryGetValue(reason, out int c);
        a.fails[reason] = c + 1;
        a.lastFail = reason;
        a.lastFailRound = "第" + RoundOf(a.stage) + "轮";
    }

    private static bool TryFire(Action a, out string error)
    {
        error = null;

        ReactionSystem rs = ReactionSystem.Instance;
        if (rs == null) { error = "找不到 ReactionSystem"; return false; }

        string args = BuildArguments(a);
        // 拼不出参数（比如 select 挑不到合适的道具）：当成"还没到时候"，继续等，不记失败
        if (args == null) return false;

        if (!rs.ExecuteToolDirect(a.toolName, args, a.stage, out error))
        {
            if (string.IsNullOrEmpty(error)) error = "执行失败";
            return false;
        }
        return true;
    }

    // ==================== 参数组装（select-- / para--） ====================

    private static string BuildArguments(Action a)
    {
        var parts = new List<string>();
        bool hasIndex = false;

        int selIndex = -1;
        if (TrySelectIndex(a, out selIndex)) { parts.Add("\"index\":" + selIndex); hasIndex = true; }
        else if (HasSelect(a)) return null;   // 写了 select 但挑不到东西 → 视为条件不满足，等下次

        foreach (string s in a.raw)
        {
            if (!s.StartsWith("para--")) continue;
            string[] t = s.Split(new[] { "--" }, System.StringSplitOptions.None);
            if (t.Length < 3) continue;
            string key = t[1];
            string val = string.Join("--", t, 2, t.Length - 2);

            // para--angle--垂直 / 躲避：按「垂直于最近那发穿甲弹的航线」现算角度。
            // 预行动看不见弹道，固定角度会把它顺着航线送出去（"自己向穿甲上送"）。
            if (key == "angle" && (val == "垂直" || val == "躲避" || val == "perpendicular"))
            {
                float? evade = PerpendicularEscapeAngle(a.stage);
                if (evade == null) return null;              // 没有来袭信息 → 这次不触发，等下一次
                val = evade.Value.ToString("0.#", CultureInfo.InvariantCulture);
            }

            if (key == "index") { hasIndex = true; }
            if (IsNumber(val)) parts.Add($"\"{key}\":{val}");
            else parts.Add($"\"{key}\":\"{val.Replace("\"", "")}\"");
        }

        if (parts.Count == 0) return "{}";
        if (!hasIndex && a.toolName == "use_prop") return null;   // use_prop 必须有 index
        return "{" + string.Join(",", parts) + "}";
    }

    private static bool HasSelect(Action a)
    {
        foreach (string s in a.raw) if (s.StartsWith("select--")) return true;
        return false;
    }

    /// <summary>select--prop--… → 选中道具的 index（1 起）；没写 select 返回 false。</summary>
    private static bool TrySelectIndex(Action a, out int index)
    {
        index = -1;
        string sel = null;
        foreach (string s in a.raw) if (s.StartsWith("select--")) { sel = s; break; }
        if (sel == null) return false;

        string[] t = sel.Split(new[] { "--" }, System.StringSplitOptions.None);
        List<PropEntry> props = MapConfig.Instance != null && a.stage < MapConfig.Instance.teamProps.Length
            ? MapConfig.Instance.teamProps[a.stage] : null;
        if (props == null || props.Count == 0) return false;

        // select--prop--index--N
        if (t.Length >= 4 && t[2] == "index")
        {
            if (int.TryParse(t[3], out int n) && n >= 1 && n <= props.Count) { index = n; return true; }
            return false;
        }
        // select--prop--smallest / biggest
        if (t.Length >= 3 && (t[2] == "smallest" || t[2] == "biggest"))
        {
            int best = -1; long bestVal = 0;
            for (int i = 0; i < props.Count; i++)
            {
                long v = props[i].value.ToLong();
                if (best < 0 || (t[2] == "smallest" ? v < bestVal : v > bestVal)) { best = i; bestVal = v; }
            }
            if (best >= 0) { index = best + 1; return true; }
            return false;
        }
        // select--prop--contain--<种类>--value--<cmp>--<数字>（也可省掉种类：contain--value--…）
        if (t.Length >= 3 && t[2] == "contain")
        {
            string kind = null; string cmp = null; long threshold = 0; bool hasValue = false;
            int p = 3;
            if (p < t.Length && !IsCmp(t[p])) { kind = t[p]; p++; }
            if (p + 1 < t.Length && t[p] == "value")
            {
                p++;
                if (p + 1 < t.Length && IsCmp(t[p])) { cmp = t[p]; hasValue = long.TryParse(t[p + 1], out threshold); }
            }
            for (int i = 0; i < props.Count; i++)
            {
                if (kind != null && props[i].item.ToString() != kind) continue;
                if (hasValue && !Compare(props[i].value.ToLong(), cmp, threshold)) continue;
                index = i + 1;
                return true;
            }
            return false;
        }
        return false;
    }

    // ==================== 条件解析与评估 ====================

    private static string ParseCheck(Action a)
    {
        foreach (string s in a.raw)
        {
            if (string.IsNullOrWhiteSpace(s)) return "有空的条件串";
            string head = s.Split(new[] { "--" }, System.StringSplitOptions.None)[0];
            if (head == "select" || head == "para" || head == "reuse" || head == "all" || head == "any" || head == "not") continue;
            return $"不认识的段名「{head}」（只能是 all/any/not/select/para/reuse）";
        }
        return null;
    }

    /// <summary>所有条件串（多条之间按 all 组合）都满足才算 ready。</summary>
    private static bool ConditionsMet(Action a, out string currentText)
    {
        currentText = "";
        foreach (string s in a.raw)
        {
            string head = s.Split(new[] { "--" }, System.StringSplitOptions.None)[0];
            if (head != "all" && head != "any" && head != "not") continue;

            if (!EvalLine(a, s, out bool ok, out string text))
            {
                currentText = "条件写法有问题";
                return false;
            }
            currentText = text;
            if (!ok) return false;
        }
        return true;
    }

    private static bool EvalLine(Action a, string line, out bool ok, out string text)
    {
        ok = false; text = "";
        string[] t = line.Split(new[] { "--" }, System.StringSplitOptions.None);
        if (t.Length < 4) { text = "条件太短"; return false; }

        string logic = t[0];
        string subject = t[1];
        long now = 0; string show = "";

        switch (subject)
        {
            case "upgrade":
                now = (long)Energy(a.stage);
                show = "升级能量 " + now;
                break;
            case "left":
                now = SlotsLeft(a.stage);
                show = "空槽 " + now;
                break;
            case "slot":
                now = SlotLimit();
                show = "槽位上限 " + now;
                break;
            case "shield":
                now = ShieldOf(a.stage).ToLong();
                show = "护盾 " + now;
                break;
            case "bullet":
                now = BulletOf(a.stage).ToLong();
                show = "子弹量 " + now;
                break;
            case "marble":
                now = marbleCount.TryGetValue(a.stage, out int mc) ? mc : 0;
                show = "弹珠 " + now;
                break;
            case "territory":
                now = territory.TryGetValue(a.stage, out long tp) ? tp : 0;
                show = "领土 " + now;
                break;
            case "threat":
                now = (long)(threatDist.TryGetValue(a.stage, out float td) ? td : 99f);
                show = "最近敌方领土距离 " + now;
                break;
            case "round":
                now = RoundOf(a.stage);
                show = "回合 " + now;
                break;
            case "enemyNum":
                now = Mathf.Max(0, Towel.AllTowel.Count - 1);
                show = "存活敌人 " + now;
                break;
            case "moveCost":
                now = (long)Cost(a.stage, MarbleManager.EnergyAction.Move);
                show = "本次移动价格 " + now;
                break;
            case "whisperCost":
                now = (long)Cost(a.stage, MarbleManager.EnergyAction.Whisper);
                show = "本次悄悄话价格 " + now;
                break;
            case "pos":
                if (t.Length < 5) { text = "pos 需要 pos--x/y--more--数字"; return false; }
                now = (long)(t[2] == "x" ? Pos(a.stage).x : Pos(a.stage).y);
                show = "坐标" + t[2] + " " + now;
                return Finish(logic, now, t[3], t[4], show, out ok, out text);
            case "moving":
                if (t.Length < 4) { text = "moving 需要 moving--is--true/false"; return false; }
                bool movingNow = IsMoving(a.stage);
                bool want = t[3] == "true";
                ok = movingNow == want;
                text = $"正在移动 {movingNow}";
                return true;
            case "prop":
                return EvalProp(a, t, logic, out ok, out text);
            case "warn":
                return EvalWarn(a, t, logic, out ok, out text);
            case "threatPos":
                if (t.Length < 5) { text = "threatPos 需要 threatPos--x/y--more--数字"; return false; }
                Vector2 tpPos = threatPos.TryGetValue(a.stage, out Vector2 pv) ? pv : Vector2.zero;
                now = (long)(t[2] == "x" ? tpPos.x : tpPos.y);
                show = "最近敌方领土" + t[2] + " " + now;
                return Finish(logic, now, t[3], t[4], show, out ok, out text);
            default:
                text = $"不认识的对象「{subject}」";
                return false;
        }

        return Finish(logic, now, t[2], t[3], show, out ok, out text);
    }

    private static bool EvalProp(Action a, string[] t, string logic, out bool ok, out string text)
    {
        ok = false; text = "";
        List<PropEntry> props = a.stage < MapConfig.Instance.teamProps.Length ? MapConfig.Instance.teamProps[a.stage] : null;
        props ??= new List<PropEntry>();

        // prop--count--more--2
        if (t.Length >= 4 && t[2] == "count")
        {
            long c = props.Count;
            return Finish(logic, c, t[3], t.Length > 4 ? t[4] : "0", "道具数 " + c, out ok, out text);
        }
        // prop--contain--[种类]--value--<cmp>--<数字>
        if (t.Length >= 3 && t[2] == "contain")
        {
            string kind = null; string cmp = null; long threshold = 0; bool hasValue = false;
            int p = 3;
            if (p < t.Length && !IsCmp(t[p])) { kind = t[p]; p++; }
            if (p + 1 < t.Length && t[p] == "value" && IsCmp(t[p + 1]))
            {
                cmp = t[p + 1];
                hasValue = long.TryParse(t[p + 2 < t.Length ? p + 2 : p + 1], out threshold);
            }
            bool found = false;
            foreach (PropEntry pe in props)
            {
                if (kind != null && pe.item.ToString() != kind) continue;
                if (hasValue && !Compare(pe.value.ToLong(), cmp, threshold)) continue;
                found = true; break;
            }
            ok = logic == "not" ? !found : found;
            text = (kind ?? "任意") + (hasValue ? $"数值{cmp}{threshold}" : "") + " 的道具" + (found ? " 有" : " 没有");
            return true;
        }
        text = "prop 只支持 count / contain";
        return false;
    }

    private static bool EvalWarn(Action a, string[] t, string logic, out bool ok, out string text)
    {
        ok = false; text = "";
        // warn--count--more--0   /  warn--type--穿甲--eta--less--5
        if (t.Length >= 4 && t[2] == "count")
        {
            long c = (warnShellCount.TryGetValue(a.stage, out int sc) ? sc : 0)
                   + (warnBallCount.TryGetValue(a.stage, out int bc) ? bc : 0);
            return Finish(logic, c, t[3], t.Length > 4 ? t[4] : "0", "预警数 " + c, out ok, out text);
        }
        // all--warn--type--穿甲--eta--less--5  →  t=[all, warn, type, 穿甲, eta, less, 5]
        if (t.Length >= 7 && t[2] == "type" && t[4] == "eta")
        {
            bool shell = t[3] == "穿甲";
            float eta = shell
                ? (warnShellEta.TryGetValue(a.stage, out float se) ? se : 99f)
                : (warnBallEta.TryGetValue(a.stage, out float be) ? be : 99f);
            long now10 = (long)(eta * 10f);                       // 秒 ×10，避免小数比较
            long limit10 = (long)(ParseNumber(t[6]) * 10);
            bool r = Compare(now10, t[5], limit10);
            ok = logic == "not" ? !r : r;
            text = $"{t[3]}最近还有 {eta:0.0} 秒";
            return true;
        }
        text = "warn 只支持 count / type--<种类>--eta--<cmp>--<秒>";
        return false;
    }

    private static bool Finish(string logic, long now, string cmp, string valueText, string show,
        out bool ok, out string text, long? overrideLimit = null)
    {
        long limit = overrideLimit ?? ParseNumber(valueText);
        bool r = Compare(now, cmp, limit);
        ok = logic == "not" ? !r : r;
        text = show;
        return true;
    }

    private static bool Compare(long now, string cmp, long limit)
    {
        switch (cmp)
        {
            case "more": return now > limit;
            case "less": return now < limit;
            case "moreEqual": return now >= limit;
            case "lessEqual": return now <= limit;
            case "equal": return now == limit;
            default: return false;
        }
    }

    private static bool IsCmp(string s) => s == "more" || s == "less" || s == "moreEqual" || s == "lessEqual" || s == "equal";

    private static long ParseNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Trim();
        double mul = 1;
        if (s.EndsWith("K") || s.EndsWith("k")) { mul = 1e3; s = s.Substring(0, s.Length - 1); }
        else if (s.EndsWith("M") || s.EndsWith("m")) { mul = 1e6; s = s.Substring(0, s.Length - 1); }
        else if (s.EndsWith("B") || s.EndsWith("b")) { mul = 1e9; s = s.Substring(0, s.Length - 1); }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return (long)(v * mul);
        return 0;
    }

    private static bool IsNumber(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    // ---- 取实时状态的小工具 ----
    private static float Energy(int stage) => MarbleManager.Instance != null ? MarbleManager.Instance.GetUpgradeEnergy(stage) : 0f;
    private static float Cost(int stage, MarbleManager.EnergyAction action)
        => MarbleManager.Instance != null ? MarbleManager.Instance.GetActionEnergyCost(stage, action) : 0f;
    private static int SlotLimit() => MapConfig.Instance != null ? MapConfig.Instance.propLimit : 0;
    private static int SlotsLeft(int stage)
    {
        if (MapConfig.Instance == null) return 0;
        List<PropEntry> props = MapConfig.Instance.teamProps[stage];
        return Mathf.Max(0, MapConfig.Instance.propLimit - (props != null ? props.Count : 0));
    }
    private static Towel Tower(int stage) => Towel.AllTowel.TryGetValue(stage, out Towel t) ? t : null;
    private static HugeInt ShieldOf(int stage) { Towel t = Tower(stage); return t != null ? t.shield_value : new HugeInt(0); }
    private static HugeInt BulletOf(int stage) { Towel t = Tower(stage); return t != null ? t.value : new HugeInt(0); }
    private static Vector2 Pos(int stage) { Towel t = Tower(stage); return t != null ? (Vector2)t.transform.position : Vector2.zero; }
    private static bool IsMoving(int stage) { Towel t = Tower(stage); return t != null && t.IsMoving; }

    private static bool IsKnownTool(string name)
    {
        return name == "use_prop" || name == "merge_prop" || name == "move_turret"
            || name == "control_turret" || name == "whisper";
    }

    private static string ReuseText(int reuse)
        => reuse < 0 ? "，reuse -1（永久保留）" : reuse == 0 ? "，reuse 0（执行一次后自动取消）" : $"，reuse {reuse}（存活 {reuse} 个 AI 轮次）";

    // ==================== 给 AI 看的文本 ====================

    private static string DescribeConditions(Action a)
    {
        var conds = new List<string>();
        foreach (string s in a.raw)
        {
            string head = s.Split(new[] { "--" }, System.StringSplitOptions.None)[0];
            if (head == "all" || head == "any" || head == "not") conds.Add(s);
        }
        return conds.Count == 0 ? "（无条件）" : string.Join(" && ", conds);
    }

    /// <summary>情报里每回合列出的预行动面板。</summary>
    public static string DescribeFor(int stage)
    {
        if (!byStage.TryGetValue(stage, out List<Action> list) || list.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("〔预行动 ").Append(list.Count).Append('/').Append(MaxPerStage).Append(" 条〕");
        foreach (Action a in list)
        {
            sb.Append("\n id").Append(a.id).Append(' ').Append(a.toolName)
              .Append(" ｜ 条件 ").Append(DescribeConditions(a));
            ConditionsMet(a, out string cur);
            sb.Append("（当前 ").Append(string.IsNullOrEmpty(cur) ? "—" : cur).Append('）');
            sb.Append(" ｜ 已执行 ").Append(a.executed).Append(" 次");
            sb.Append(" ｜ ").Append(ReuseText(a.reuse).TrimStart('，'));
            if (a.paused) sb.Append(" ｜ 已暂停");
            if (a.fails.Count > 0)
            {
                sb.Append(" ｜ 触发失败");
                int total = 0; string top = null; int topN = 0;
                foreach (var f in a.fails) { total += f.Value; if (f.Value > topN) { topN = f.Value; top = f.Key; } }
                sb.Append(" ×").Append(total).Append("（").Append(top).Append("；最近 ").Append(a.lastFail)
                  .Append(" @").Append(a.lastFailRound).Append("）");
            }
        }
        return sb.ToString();
    }

    public static string ListText(int stage)
    {
        string text = DescribeFor(stage);
        return text ?? "（当前没有预行动）";
    }
}
