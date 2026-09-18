using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;


public class InformGetter : MonoBehaviour
{
    public MarbleManager MarbleItem;
    public MapConfig MapItem;
    //接下来数据收集的方法
    //增量收集
    public static Dictionary<int, List<ItemType>> Oitems = new();//外在数据
    public static Dictionary<int, List<ItemType>> Iitems = new();//内部数据，目前没用

    //弹珠注册表：按阵营收集，生成时注册、销毁时自动清理
    public static Dictionary<int, List<MarbleType>> MarbleItems = new();

    //guid → Transform 全局查找表，供AI指令通过guid定位对象
    public static Dictionary<string, Transform> GuidToTransform = new();

    // 伤害统计子系统：target 阵营 -> (source 阵营 -> 伤害值)。
    // 阵营私有：GetInfo 只输出请求方自己的受击统计，输出完成后整体清空。
    public static Dictionary<int, List<DamageSourceInfo>> DamageStats = new();

    // 炮塔控制自动断开提示：阵营 -> 断开原因。GetInfo 输出一次后清除。
    public static Dictionary<int, string> TurretControlLostReason = new();

    // 炮塔初始位置：阵营 -> 开局世界坐标。开局注册一次，之后永不变化（AI 只知道开局坐标）。
    public static Dictionary<int, Vector2> InitialTurretPositions = new();

    // 撞击情报：攻击方阵营 -> 本轮新增的撞击记录。阵营私有，只发给造成撞击的一方，GetInfo 输出后清空。
    public static Dictionary<int, List<ImpactInfo>> ImpactStats = new();

    // 炮塔移动结束/中断提示：阵营 -> 原因。GetInfo 输出一次后清除。
    public static Dictionary<int, string> TurretMoveNotice = new();

    public const string ImpactKindShield = "护盾";
    public const string ImpactKindTurret = "炮塔本体";
    public const string ImpactSourceBall = "大球";
    public const string ImpactSourceBullet = "子弹";

    // 一轮缓冲：AIContents 装的是“上一轮”的发言，每轮结束时由 CommitRoundSpeeches 整体重建。
    public static Dictionary<int, string> AIContents = new();

    // 本轮暂存的发言（Say / 遗言 / 赢家感言写入），轮末提交进 AIContents。
    private static readonly Dictionary<int, string> PendingSpeech = new();

    // 本轮击杀事件，写进标签外的【战况】；输出一次后清空。
    private static readonly List<string> KillEvents = new();

    /// <summary>记录本轮某阵营说过的话（公开发言 / 遗言 / 赢家感言）。轮末由 CommitRoundSpeeches 提交。</summary>
    public static void StageSpeech(int stage, string content)
    {
        if (stage < 0 || string.IsNullOrWhiteSpace(content)) return;
        PendingSpeech[stage] = content.Trim();
    }

    /// <summary>每轮结束调用：清空上一轮发言缓冲，把本轮暂存的发言搬进去（下一轮读到的就是刚好上一轮的发言）。</summary>
    public static void CommitRoundSpeeches()
    {
        AIContents.Clear();
        foreach (var kv in PendingSpeech)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value)) AIContents[kv.Key] = kv.Value;
        }
        PendingSpeech.Clear();
    }

    /// <summary>记录一次击杀事件（写进标签外的【战况】，输出后清空）。</summary>
    public static void PushKillEvent(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) KillEvents.Add(text.Trim());
    }

    #region 注册
    //注册一个新弹珠，由MarbleManager在生成时调用
    public static void AddMarble(int stage, Marble m)
    {
        if (!MarbleItems.ContainsKey(stage)) MarbleItems[stage] = new();
        var mt = new MarbleType(m);
        MarbleItems[stage].Add(mt);
        GuidToTransform[mt.guid] = mt.item;
    }

    //有两种调用  1.初始化的调用√  2.新增加的大球的调用√
    public static void AddItem(int stage, ItemType item, bool Out = true)
    {
        if (Out)
        {
            if (!Oitems.ContainsKey(stage)) Oitems[stage] = new();
            Oitems[stage].Add(item);
            if (!string.IsNullOrEmpty(item.guid) && item.item != null)
                GuidToTransform[item.guid] = item.item;
        }
    }

    /// <summary>记录一次伤害：targetStage 是受击方，sourceStage 是伤害来源。</summary>
    public static void AddDamage(int targetStage, int sourceStage, string sourceGuid, string sourceDesc, HugeInt damage)
    {
        if (targetStage == sourceStage || damage <= 0) return;

        if (!DamageStats.TryGetValue(targetStage, out var list))
        {
            list = new List<DamageSourceInfo>();
            DamageStats[targetStage] = list;
        }

        DamageSourceInfo entry = list.Find(e => e.sourceStage == sourceStage && e.sourceGuid == sourceGuid && e.sourceDesc == sourceDesc);
        if (entry == null)
        {
            entry = new DamageSourceInfo { sourceStage = sourceStage, sourceGuid = sourceGuid, sourceDesc = sourceDesc, damage = 0 };
            list.Add(entry);
        }
        entry.damage += damage;
    }

    /// <summary>记录一次炮塔控制自动断开（供下一轮 GetInfo 提示 AI）。</summary>
    public static void NotifyTurretControlLost(int stage, string reason)
    {
        if (stage < 0) return;
        TurretControlLostReason[stage] = reason;
    }

    #endregion
    //获得目标stage能获得的全部信息
    public const string IntelInfoStart = "【情报信息开始】";
    public const string IntelInfoEnd = "【情报信息结束】";

    public static void GetInfo(StringBuilder builder, int stage, bool clearDamage = false)
    {
        builder.AppendLine(); builder.AppendLine(IntelInfoStart);
        // 回合数：全局第几轮 + 这张卡自己的第几轮（预行动的 reuse 按 AI 轮次算）
        builder.Append("当前回合: 全局第 ").Append(AIAgent.CurrentRound).Append(" 轮，你的第 ").Append(ReadyActionManager.RoundOf(stage)).AppendLine(" 轮");
        //自己的道具栈放最前面，AI 第一眼就能看到
        GetInfoProp(builder, stage);
        AppendTurretControlNotice(builder, stage);
        AppendTurretControlState(builder, stage);
        AppendTurretMoveNotice(builder, stage);
        AppendTurretPositions(builder, stage);
        AppendNearestEnemyTerritory(builder, stage);
        AppendReadyActionPanel(builder, stage);
        TurretControlLostReason.Remove(stage);
        TurretMoveNotice.Remove(stage);

        //全局可见信息
        foreach (var key in Oitems.Keys)
        {
            GetInfoOKey(builder, key);
        }
        AppendBallImpactWarning(builder, stage);
        AppendFinalRoundHint(builder, stage);
        //弹珠为阵营私有信息，只返回请求方自己的
        foreach (var key in MarbleItems.Keys)
        {
            if (key != stage) continue;
            GetInfoMarble(builder, key);
        }
        AppendUpgradeProgress(builder, stage);
        AppendTerritoryArea(builder);
        AppendDamageInfo(builder, stage);
        AppendImpactInfo(builder, stage);
        if (clearDamage) ClearDamageStats();
        builder.AppendLine(); builder.AppendLine(IntelInfoEnd);
    }

    /// <summary>
    /// 构建“标签外”的每轮补充消息：上一轮发言 + 存活状态 + 战况。
    /// 故意不放进【情报信息…】里：Compress() 只压带情报标记的消息，所以这条永远不会被压成 [情报压缩]，
    /// 成为 AI 的长期对局记忆（谁说过什么、谁被谁杀了）。击杀事件输出一次后清空。
    /// </summary>
    public static string BuildRoundExtra()
    {
        var sb = new StringBuilder();

        // 上一轮发言（只含上一轮真的说过话的阵营；死者的遗言只在它死后的那一轮出现一次）
        if (AIContents.Count > 0)
        {
            var keys = new List<int>(AIContents.Keys);
            keys.Sort();
            bool wrote = false;
            foreach (int s in keys)
            {
                if (string.IsNullOrWhiteSpace(AIContents[s])) continue;
                if (!wrote) { sb.AppendLine("【上一轮发言】"); wrote = true; }
                string dead = AIAgent.IsStageEliminated(s) ? "（已出局）" : "";
                sb.AppendLine($"  {s}号阵营({AIAgent.GetStageName(s)}){dead}：{AIContents[s]}");
            }
        }

        // 存活状态
        if (AIAgent.Instance != null)
        {
            List<int> ids = AIAgent.Instance.StageIds;
            var alive = new List<string>();
            var dead = new List<string>();
            foreach (int s in ids)
            {
                string text = $"{s}号({AIAgent.GetStageName(s)})";
                if (AIAgent.IsStageEliminated(s)) dead.Add(text); else alive.Add(text);
            }
            if (alive.Count + dead.Count > 0)
                sb.AppendLine("【存活状态】存活: " + string.Join("、", alive) + (dead.Count > 0 ? "｜已淘汰: " + string.Join("、", dead) : ""));
        }

        // 战况（击杀事件）
        if (KillEvents.Count > 0)
        {
            sb.AppendLine("【战况】");
            foreach (string e in KillEvents) sb.AppendLine("  " + e);
            KillEvents.Clear();
        }

        return sb.ToString().TrimEnd();
    }

    #region 领土面积
    /// <summary>
    /// 检测当前地图各阵营领土面积（像素数）。
    /// 地图每个像素代表单位1；返回数组下标为阵营阶段，area[0]为中立/未占领，area[1..N]为各阵营。
    /// TerritoryCanvas 或 MapConfig 未找到、地图未创建时返回 null。
    /// </summary>
    public static int[] GetTerritoryArea()
    {
        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return null;

        var config = MapConfig.Instance;
        if (config == null) return null;

        var map = canvas.territoryMap;
        int teamCount = config.teamColors.Count; // 含中立阶段0
        int[] area = new int[teamCount];
        for (int i = 0; i < map.Length; i++)
        {
            byte stage = map[i];
            if (stage < teamCount) area[stage]++;
        }
        return area;
    }

    /// <summary>
    /// 把各阵营领土面积拼进 StringBuilder（供 AI 上下文使用）。
    /// </summary>
    public static void AppendTerritoryArea(StringBuilder builder)
    {
        int[] area = GetTerritoryArea();
        if (area == null)
        {
            builder.AppendLine(); builder.AppendLine("当前领土面积: 地图数据不可用");
            return;
        }
        builder.AppendLine(); builder.AppendLine("各阵营领土面积(单位: 像素):");
        for (int s = 0; s < area.Length; s++)
        {
            builder.AppendLine($"  [{s}]号阵营: {area[s]}");
            ReadyActionManager.SetTerritory(s, area[s]);   // 预行动条件 territory 用（每轮刷一次，不每帧扫图）
        }
    }
    #endregion


    /// <summary>把请求方自己受到的伤害统计拼进 AI 上下文（阵营私有）。</summary>

    /// <summary>预行动面板：每回合把挂着的预行动全列出来（条件是否满足、执行了几次、失败合并）。</summary>
    private static void AppendReadyActionPanel(StringBuilder builder, int stage)
    {
        string text = ReadyActionManager.DescribeFor(stage);
        if (string.IsNullOrEmpty(text)) return;
        builder.AppendLine();
        builder.Append(text);
    }

    /// <summary>炮塔控制自动断开提示：每个阵营每次 GetInfo 最多输出一次。</summary>
    private static void AppendTurretControlNotice(StringBuilder builder, int stage)
    {
        if (!TurretControlLostReason.TryGetValue(stage, out string reason) || string.IsNullOrWhiteSpace(reason)) return;

        builder.AppendLine(); builder.AppendLine($"炮塔控制已断开：{reason}。炮塔已恢复自动旋转自动防御；除非目标进入 4 格内，不必再调用 control_turret start。");
    }

    /// <summary>手动接管中时提示：这一期间炮塔不会自动防御。</summary>
    private static void AppendTurretControlState(StringBuilder builder, int stage)
    {
        if (!Towel.AllTowel.TryGetValue(stage, out Towel self) || self == null) return;
        AimController controller = self.aimController;
        if (controller == null || !controller.IsControlling) return;

        builder.AppendLine(); builder.AppendLine("注意：你的炮塔正被手动接管瞄准，这期间它不会自动防御来袭的子弹和大球。");
    }

    /// <summary>附加请求方自己的弹珠升级进度（空槽升级）。</summary>
    private static void AppendUpgradeProgress(StringBuilder builder, int stage)
    {
        var mm = MarbleManager.Instance;
        if (mm == null || !mm.TryGetUpgradeInfo(stage, out float progress, out float cost)) return;

        builder.AppendLine(); builder.Append("{");
        builder.Append("当前己方弹珠升级进度: ");
        builder.Append(progress.ToString("0.#"));
        builder.Append(" / ");
        builder.Append(cost.ToString("0.#"));
        builder.AppendLine(); builder.Append("}");
    }
    private static void AppendDamageInfo(StringBuilder builder, int stage)
    {
        if (!DamageStats.TryGetValue(stage, out var list) || list.Count == 0) return;

        builder.AppendLine(); builder.Append("{");
        builder.Append(stage); builder.Append("号阵营受击统计: ");
        foreach (var entry in list)
        {
            builder.AppendLine(); builder.Append("(");
            builder.Append("来源 "); builder.Append(entry.sourceDesc);
            if (!string.IsNullOrEmpty(entry.sourceGuid))
            {
                builder.Append(" guid:"); builder.Append(entry.sourceGuid);
            }
            builder.Append(" 伤害 "); builder.Append(entry.damage.ToShortString());
            builder.Append(")");
        }
        builder.AppendLine(); builder.Append("}");
    }

    #region 炮塔位置与撞击情报
    /// <summary>注册炮塔初始位置（开局调用一次；此后再不更新，AI 只知道开局坐标）。</summary>
    public static void RegisterInitialPosition(int stage, Vector2 pos) => InitialTurretPositions[stage] = pos;

    /// <summary>
    /// 撞击点的合并粒度：落在同一个 0.1×0.1 方格（也就是每个点代表 ±0.05 的方形范围）内、
    /// 同一目标同一来源的撞击会合并成一条。扫射一轮能打出上千颗子弹，不合并单轮情报能到 17 万字符。
    /// </summary>
    public const float ImpactMergeCell = 0.1f;

    /// <summary>把撞击点吸到 0.1 网格上：返回的点代表以它为中心、±0.05 的方形。</summary>
    private static Vector2 SnapImpactPoint(Vector2 point)
        => new Vector2(Mathf.Round(point.x / ImpactMergeCell) * ImpactMergeCell,
                       Mathf.Round(point.y / ImpactMergeCell) * ImpactMergeCell);

    /// <summary>
    /// 记录一次撞击情报，只发给造成撞击的一方（攻击方）。targetKind 用 ImpactKindShield / ImpactKindTurret。
    /// 同一目标 + 同一来源类型 + 落在同一格（±0.05）的撞击并进同一条：次数累加，
    /// 护盾记「第一次撞击前」和「最后一次撞击后」。
    /// </summary>
    public static void AddImpact(int attackerStage, int targetStage, string targetKind, Vector2 point,
        HugeInt shieldBefore, HugeInt shieldAfter, string sourceKind)
    {
        if (attackerStage <= 0 || targetStage <= 0 || attackerStage == targetStage) return;
        if (!ImpactStats.TryGetValue(attackerStage, out List<ImpactInfo> list))
        {
            list = new List<ImpactInfo>();
            ImpactStats[attackerStage] = list;
        }

        Vector2 cell = SnapImpactPoint(point);
        ImpactInfo same = list.Find(e =>
            e.targetStage == targetStage && e.targetKind == targetKind && e.sourceKind == sourceKind &&
            Mathf.Abs(e.point.x - cell.x) < 0.0001f && Mathf.Abs(e.point.y - cell.y) < 0.0001f);

        if (same != null)
        {
            same.count++;
            same.shieldAfter = shieldAfter;   // 留"最后一次"之后的盾值
            return;
        }

        list.Add(new ImpactInfo
        {
            targetStage = targetStage,
            targetKind = targetKind,
            point = cell,
            shieldBefore = shieldBefore,
            shieldAfter = shieldAfter,
            sourceKind = sourceKind,
            count = 1
        });
    }

    public static void ClearImpactStats() => ImpactStats.Clear();

    /// <summary>炮塔移动结束/中断提示（抵达、撞墙、超时），GetInfo 输出一次后清除。</summary>
    public static void NotifyTurretMoveDone(int stage, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        TurretMoveNotice[stage] = reason;
    }

    private static void AppendTurretMoveNotice(StringBuilder builder, int stage)
    {
        if (!TurretMoveNotice.TryGetValue(stage, out string reason) || string.IsNullOrWhiteSpace(reason)) return;
        builder.AppendLine(); builder.AppendLine($"炮塔移动已结束：{reason}。");
    }

    /// <summary>炮塔位置情报：全体炮塔的初始位置（固定不变）+ 自己炮塔的当前位置/子弹量/护盾/移动状态/最大移动距离。</summary>
    private static void AppendTurretPositions(StringBuilder builder, int stage)
    {
        if (InitialTurretPositions.Count == 0) return;

        builder.AppendLine(); builder.AppendLine("各炮塔初始位置(开局坐标，之后不会再更新):");
        var keys = new List<int>(InitialTurretPositions.Keys);
        keys.Sort();
        foreach (int s in keys)
        {
            Vector2 p = InitialTurretPositions[s];
            builder.AppendLine($"  [{s}]号阵营({AIAgent.GetStageName(s)}): ({p.x.ToString("0.00")}, {p.y.ToString("0.00")})");
        }

        if (Towel.AllTowel.TryGetValue(stage, out Towel self) && self != null)
        {
            Vector2 p = self.transform.position;
            builder.Append("你的炮塔: 当前位置 (");
            builder.Append(p.x.ToString("0.00")); builder.Append(", "); builder.Append(p.y.ToString("0.00"));
            // 子弹量与护盾：1.0.1 里这两个是随「场上信息」公开的，现在只报给自己（私有情报）。
            // 护盾碎时写「已破碎」而不是 0，跟 Situation/1.0.1 的口径一致。
            builder.Append(")，当前子弹量 "); builder.Append(self.value.ToShortString());
            builder.Append("，护盾 ");
            builder.Append(self.shield_value > 0 ? self.shield_value.ToShortString() : "已破碎");
            builder.Append("，最大移动距离 "); builder.Append(self.MaxMoveDistance.ToString("0.00"));
            MarbleManager mm = MarbleManager.Instance;
            MapConfig cfg = MapConfig.Instance;
            if (mm != null)
            {
                // 移动 / 悄悄话都涨价：报"这一次"的价格，而不是 MapConfig 上的基础价，
                // 否则 AI 会按第一次的价格做规划，实际扣费比它以为的多。（公开发言不花点数，不用报）
                builder.Append("，升级能量 ");
                builder.Append(mm.GetUpgradeEnergy(stage).ToString("0.#"));
                builder.Append("（移动和悄悄话共用这一个池子，同一个动作每用一次价格 ×");
                builder.Append(cfg != null ? cfg.actionCostGrowth.ToString("0.##") : "1.5");
                builder.Append("）");
                builder.Append("：移动这次要 "); builder.Append(mm.GetActionEnergyCost(stage, MarbleManager.EnergyAction.Move).ToString("0.#"));
                builder.Append("（已移 "); builder.Append(mm.GetActionUseCount(stage, MarbleManager.EnergyAction.Move)); builder.Append(" 次）");
                if (cfg != null && cfg.whisperEnergyCost > 0f)
                {
                    builder.Append("，悄悄话这次要 "); builder.Append(mm.GetActionEnergyCost(stage, MarbleManager.EnergyAction.Whisper).ToString("0.#"));
                    builder.Append("（每 "); builder.Append(Mathf.Max(1, cfg.whisperCooldownRounds)); builder.Append(" 回合一次，开局就在冷却中）");
                }
            }
            builder.Append("，可移动范围 x,y ∈ [-"); builder.Append(self.MoveBound.ToString("0.00"));
            builder.Append(", "); builder.Append(self.MoveBound.ToString("0.00"));
            builder.Append("]，状态: "); builder.Append(self.MoveStateText);
            // 无敌倒计时：情报本身滞后一轮（= 面板上的轮间隔），剩余时间不够一轮的就别报了——报过去也已经过期。
            float invincibleLeft = self.InvincibleRemaining;
            if (invincibleLeft > AIAgent.RoundInterval)
            {
                builder.Append("，无敌剩余 "); builder.Append(invincibleLeft.ToString("0.#")); builder.Append(" 秒");
            }
            builder.AppendLine();
        }
        builder.AppendLine("(只有自己的位置是实时的。敌方炮塔会移动：近处靠 move_turret 预览附带的视野截图直接看，更远处只能靠撞击情报反推。)");
    }

    /// <summary>撞击情报：只输出请求方自己造成的撞击（撞的是谁、撞击点、护盾前后大小）。</summary>
    private static void AppendImpactInfo(StringBuilder builder, int stage)
    {
        if (!ImpactStats.TryGetValue(stage, out List<ImpactInfo> list) || list.Count == 0) return;

        builder.AppendLine(); builder.AppendLine("撞击情报(你自己打出去的撞击，只有你能看到这些；撞击点带 ±0.05 的方格误差，同一格里的多次撞击已合并并标了次数):");
        foreach (ImpactInfo e in list)
        {
            builder.AppendLine();
            builder.Append("  你的"); builder.Append(e.sourceKind);
            builder.Append("撞上 "); builder.Append(e.targetStage);
            builder.Append("号阵营("); builder.Append(AIAgent.GetStageName(e.targetStage));
            builder.Append(") 的"); builder.Append(e.targetKind);
            builder.Append("：撞击点 ("); builder.Append(e.point.x.ToString("0.00"));
            builder.Append(", "); builder.Append(e.point.y.ToString("0.00")); builder.Append(")");
            builder.Append("±").Append((ImpactMergeCell * 0.5f).ToString("0.00"));   // 同一个方格里的撞击已经合并
            if (e.count > 1) { builder.Append("，同类 "); builder.Append(e.count); builder.Append(" 次"); }
            if (e.targetKind == ImpactKindShield)
            {
                builder.Append("，护盾 撞击前 "); builder.Append(e.shieldBefore.ToShortString());
                builder.Append("、撞击后 "); builder.Append(e.shieldAfter.ToShortString());
            }
            else
            {
                builder.Append("，对方没有护盾");
            }
        }
        builder.AppendLine();
    }
    #endregion

    #region 终局提示
    public const string FinalRoundHint = "【终局提示】场上只剩你一个阵营了：把还在飞的敌方大球、穿甲弹和子弹清掉，再把领土刷到 98%，这局才会结束；别在最后被反杀。";

    /// <summary>场上是否还有敌方（非 stage 的）未被摧毁的大球或穿甲弹；判据同撞击预警（读 BallPainter.game_item_name）。</summary>
    public static bool HasEnemyBigBall(int stage)
    {
        foreach (var kv in Oitems)
        {
            if (kv.Key == stage) continue;

            foreach (ItemType item in kv.Value)
            {
                // 大球与穿甲弹都是"带数值、还在场上"的弹体，终局清场必须一起算，
                // 否则可能在被穿甲弹命中的前一刻判定游戏结束。
                if (RollingItemName(item) != null) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 终局提示：只剩请求方一个阵营、且还有敌方大球或领土还没到 98% 时，在情报正文里多拼一行普通文本。
    /// </summary>
    private static void AppendFinalRoundHint(StringBuilder builder, int stage)
    {
        if (AIAgent.Instance == null) return;
        if (!AIAgent.Instance.IsSoloWinner(stage)) return;

        float percent = 0f;
        int[] area = GetTerritoryArea();
        if (area != null && stage >= 0 && stage < area.Length)
        {
            int total = 0;
            for (int i = 0; i < area.Length; i++) total += area[i];
            if (total > 0) percent = area[stage] * 100f / total;
        }

        bool needBall = HasEnemyBigBall(stage);
        bool needMore = percent < 98f;
        if (!needBall && !needMore) return;

        builder.AppendLine();
        builder.AppendLine(needBall
            ? $"【终局提示】场上只剩你一个阵营了：先把还在飞的敌方大球和子弹清掉，再把领土刷到 98%（当前 {percent:0.#}%）就结束，别在最后被反杀。"
            : $"【终局提示】场上只剩你一个阵营了：把领土刷到 98% 就结束（当前 {percent:0.#}%）。");
    }
    #endregion

    #region 弹体撞击预警（大球 / 穿甲弹）
    // 轨迹推演参数：最多预测 12 秒、步长 0.02 秒。“几秒后撞上”按近似计算。
    private const float BallWarningMaxPredictTime = 12f;
    private const float BallWarningStep = 0.02f;
    /// <summary>炮塔本体判定半径，与 BulletManager 里算撞击用的是同一个口径。</summary>
    private const float TurretBodyRadius = 0.4f;

    /// <summary>
    /// 这项是不是“带数值在场上滚/飞的弹体”（大球 / 穿甲弹）：是就返回名字，否则 null。
    /// 名字统一读 BallPainter.game_item_name —— 伤害与撞击情报的口径也是它，别再按 description 猜。
    /// </summary>
    private static string RollingItemName(ItemType item)
    {
        if (item == null || item.item == null) return null;
        BallPainter bp = item.stageValue as BallPainter;
        if (bp == null) bp = item.item.GetComponent<BallPainter>();
        string name = bp != null ? bp.game_item_name : item.description;
        return (name == "大球" || name == "穿甲") ? name : null;
    }

    /// <summary>
    /// 弹体撞击预警：
    /// - 大球：直线 + 地图边界反弹 + 敌方护盾镜面反弹，算它几秒后第一次撞上请求方护盾（盾没了就不报，球会被盾挡下）。
    /// - 穿甲弹：**穿过护盾**直取炮塔本体，所以不算护盾反弹、也不算盾内减速与离盾偏转，直接算几秒后打到你的炮塔本体；
    ///   护盾碎了同样要报（盾对它没有意义）。
    /// 只在预测窗口内会命中的才输出，格式仿照“最近敌方领土”块。
    /// </summary>
    private static void AppendBallImpactWarning(StringBuilder builder, int stage)
    {
        var config = MapConfig.Instance;
        var canvas = TerritoryCanvas.Instance;
        if (config == null || canvas == null) return;
        if (!Towel.AllTowel.TryGetValue(stage, out Towel self) || self == null) return;

        Vector2 mapCenter = canvas.transform.position;
        float mapHalf = config.worldSize * 0.5f;
        Vector2 selfPos = self.transform.position;
        float selfShieldR = self.shield != null ? self.shield.transform.lossyScale.x : 0f;
        bool shieldAlive = self.shield_value > 0 && selfShieldR > 0f;

        // 预行动条件 warn--… 用的统计（也只在这里每轮刷一次）
        int shellCount = 0, ballCount = 0;
        float shellEta = 99f, ballEta = 99f;

        foreach (var kv in Oitems)
        {
            if (kv.Key == stage) continue;

            foreach (ItemType item in kv.Value)
            {
                string itemName = RollingItemName(item);
                if (itemName == null) continue;
                bool isShell = itemName == "穿甲";
                if (!isShell && !shieldAlive) continue;

                BallPainter bp = item.stageValue as BallPainter;
                float bodyR = bp != null
                    ? Mathf.Max(item.item.lossyScale.x, item.item.lossyScale.y) * bp.baseWorldRadius
                    : 0f;
                Vector2 vel = item.rb != null ? item.rb.velocity : Vector2.zero;

                float impactT = isShell
                    ? PredictShellImpact(item.item.position, vel, bodyR, mapCenter, mapHalf, selfPos, TurretBodyRadius)
                    : PredictShieldImpact(item.item.position, vel, bodyR, mapCenter, mapHalf, stage, selfPos, selfShieldR);
                if (impactT < 0f) continue;

                builder.AppendLine(); builder.Append("{");
                builder.Append(isShell ? "穿甲弹撞击预警: " : "大球撞击预警: ");
                builder.Append(AIAgent.GetStageName(kv.Key));
                builder.Append(isShell ? "的穿甲弹(" : "的大球(");
                builder.Append("guid: "); builder.Append(item.guid);
                builder.Append(", 数值 "); builder.Append(item.value);
                builder.Append(", 直径 "); builder.Append((bodyR * 2).ToString("0.0"));
                builder.Append(")预计 "); builder.Append(impactT.ToString("0.0"));
                if (isShell)
                {
                    builder.Append(" 秒后打到你的炮塔本体(炮塔本体直径 ");
                    builder.Append((TurretBodyRadius * 2).ToString("0.0")); builder.Append(")");
                }
                else
                {
                    builder.Append(" 秒后第一次撞上你的护盾(护盾当前直径 ");
                    builder.Append((selfShieldR * 2).ToString("0.0")); builder.Append(")");
                }
                builder.AppendLine(); builder.Append("}");
                if (isShell) { shellCount++; if (impactT < shellEta) shellEta = impactT; }
                else { ballCount++; if (impactT < ballEta) ballEta = impactT; }
            }
        }

        ReadyActionManager.SetWarnings(stage, shellCount, shellEta, ballCount, ballEta);
    }

    /// <summary>
    /// 模拟穿甲弹轨迹，返回首次打中 targetPos（炮塔本体）的预计时间（秒）；窗口内打不中返回 -1。
    /// 与大球不同：穿甲弹**穿过护盾**，所以这里不算护盾反弹、也不算盾内减速与离盾偏转
    /// （那两条本来就会让预测变成瞎猜，不如按"最直的一条路"报）；只保留直线运动 + 地图边界反弹。
    /// </summary>
    private static float PredictShellImpact(Vector2 pos, Vector2 vel, float shellR, Vector2 mapCenter, float mapHalf, Vector2 targetPos, float targetR)
    {
        if (vel.sqrMagnitude < 0.0001f) return -1f;

        float totalR = shellR + targetR;
        if ((pos - targetPos).sqrMagnitude <= totalR * totalR) return 0f;

        float minX = mapCenter.x - mapHalf + shellR, maxX = mapCenter.x + mapHalf - shellR;
        float minY = mapCenter.y - mapHalf + shellR, maxY = mapCenter.y + mapHalf - shellR;
        Vector2 v = vel;

        for (float t = BallWarningStep; t <= BallWarningMaxPredictTime; t += BallWarningStep)
        {
            pos += v * BallWarningStep;

            if (pos.x < minX) { pos.x = minX; if (v.x < 0) v.x = -v.x; }
            else if (pos.x > maxX) { pos.x = maxX; if (v.x > 0) v.x = -v.x; }
            if (pos.y < minY) { pos.y = minY; if (v.y < 0) v.y = -v.y; }
            else if (pos.y > maxY) { pos.y = maxY; if (v.y > 0) v.y = -v.y; }

            if ((pos - targetPos).sqrMagnitude <= totalR * totalR) return t;
        }
        return -1f;
    }

    /// <summary>
    /// 模拟大球轨迹，返回首次命中 targetStage 护盾的预计时间（秒）；预测窗口内撞不上返回 -1。
    /// 反弹均为近似：边界按子弹同规则反射，敌方护盾按镜面反弹，速度大小不变。
    /// </summary>
    private static float PredictShieldImpact(Vector2 pos, Vector2 vel, float ballR, Vector2 mapCenter, float mapHalf, int stage, Vector2 shieldPos, float shieldR)
    {
        if (vel.sqrMagnitude < 0.0001f) return -1f;

        float totalR = ballR + shieldR;
        // 已经贴脸：直接按即将命中报告
        if ((pos - shieldPos).sqrMagnitude <= totalR * totalR) return 0f;

        float minX = mapCenter.x - mapHalf + ballR, maxX = mapCenter.x + mapHalf - ballR;
        float minY = mapCenter.y - mapHalf + ballR, maxY = mapCenter.y + mapHalf - ballR;
        Vector2 v = vel;

        for (float t = BallWarningStep; t <= BallWarningMaxPredictTime; t += BallWarningStep)
        {
            pos += v * BallWarningStep;

            // 地图边界反弹（球心按半径留边）
            if (pos.x < minX) { pos.x = minX; if (v.x < 0) v.x = -v.x; }
            else if (pos.x > maxX) { pos.x = maxX; if (v.x > 0) v.x = -v.x; }
            if (pos.y < minY) { pos.y = minY; if (v.y < 0) v.y = -v.y; }
            else if (pos.y > maxY) { pos.y = maxY; if (v.y > 0) v.y = -v.y; }

            // 敌方护盾反弹：只处理正在接近且进入相切范围的球；同队盾无碰撞自然跳过
            foreach (var tkv in Towel.AllTowel)
            {
                if (tkv.Key == stage || tkv.Value == null || tkv.Value.isDead || tkv.Value.shield_value <= 0) continue;
                Vector2 sp = tkv.Value.transform.position;
                float sr = tkv.Value.shield != null ? tkv.Value.shield.transform.lossyScale.x : 0f;
                if (sr <= 0f) continue;

                Vector2 diff = pos - sp;
                float rSum = ballR + sr;
                if (diff.sqrMagnitude < rSum * rSum && Vector2.Dot(v, diff) < 0f)
                {
                    Vector2 n = diff.normalized;
                    pos = sp + n * rSum;
                    v = v - 2f * Vector2.Dot(v, n) * n;
                }
            }

            // 己方护盾命中：首次进入相切范围即报告
            if ((pos - shieldPos).sqrMagnitude <= totalR * totalR) return t;
        }
        return -1f;
    }
    #endregion

    private const float EnemyTerritoryDangerDistance = 3f;
    /// <summary>找出距离请求方基地最近的敌方领土，报告所属阵营、世界坐标、距离和相对方位。距离<=3标为危险。</summary>
    private static void AppendNearestEnemyTerritory(StringBuilder builder, int stage)
    {
        var canvas = TerritoryCanvas.Instance;
        var config = MapConfig.Instance;
        if (canvas == null || config == null || !canvas.territoryMap.IsCreated) return;
        if (!Towel.AllTowel.TryGetValue(stage, out Towel self) || self == null) return;

        int res = config.resolution;
        float ms = config.worldSize;
        Vector2 origin = canvas.transform.position; // 地形 Quad 中心的世界坐标（当前场景为原点）
        Vector2 selfPos = self.transform.position;
        var map = canvas.territoryMap;

        float bestSq = float.MaxValue;
        int bestStage = -1;
        Vector2 bestWorld = Vector2.zero;

        for (int i = 0; i < map.Length; i++)
        {
            byte s = map[i];
            if (s == stage || s == 0 || s >= config.teamColors.Count) continue;

            int px = i % res;
            int py = i / res;
            Vector2 world = origin + new Vector2((px + 0.5f) / res * ms - ms * 0.5f, (py + 0.5f) / res * ms - ms * 0.5f);
            float dx = world.x - selfPos.x;
            float dy = world.y - selfPos.y;
            float dSq = dx * dx + dy * dy;
            if (dSq < bestSq)
            {
                bestSq = dSq;
                bestStage = s;
                bestWorld = world;
            }
        }

        if (bestStage < 0) return;

        Vector2 dir = bestWorld - selfPos;
        float dist = Mathf.Sqrt(bestSq);
        string dirText = CardinalDirection(dir);

        builder.AppendLine(); builder.Append("{");
        builder.Append("距离你基地最近的敌方领土: ");
        builder.Append(bestStage); builder.Append("号阵营("); builder.Append(AIAgent.GetStageName(bestStage)); builder.Append(")，全局坐标(");
        builder.Append(bestWorld.x.ToString("0.00")); builder.Append(", ");
        builder.Append(bestWorld.y.ToString("0.00")); builder.Append(")，距离 ");
        builder.Append(dist.ToString("0.00"));
        builder.Append("｜threatPos=("); builder.Append(bestWorld.x.ToString("0.00")); builder.Append(", ");
        builder.Append(bestWorld.y.ToString("0.00")); builder.Append(")，threat="); builder.Append(dist.ToString("0.00"));
        // 给预行动系统缓存一份（它在每帧评估威胁类条件；全图扫描太贵，只在这里每轮刷一次）
        ReadyActionManager.SetThreat(stage, bestWorld, dist);
        if (dist <= EnemyTerritoryDangerDistance) builder.Append("【危险：距离3】");
        builder.Append("，相对方向 "); builder.Append(dirText);
        builder.AppendLine();
        builder.AppendLine("(这是领土边缘，不是对方炮塔的位置。想清掉它：让炮塔自动开火、或用大球/霰弹这类有指向性的道具打过来。要打对方炮塔，请用撞击情报推断出的位置，别把这块领土的坐标当成对方基地。)");
        builder.Append("}");
    }

    public static string CardinalDirection(Vector2 dir)
    {
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        if (angle >= -22.5f && angle < 22.5f) return "右";
        if (angle >= 22.5f && angle < 67.5f) return "右上";
        if (angle >= 67.5f && angle < 112.5f) return "上";
        if (angle >= 112.5f && angle < 157.5f) return "左上";
        if (angle >= 157.5f || angle < -157.5f) return "左";
        if (angle >= -157.5f && angle < -112.5f) return "左下";
        if (angle >= -112.5f && angle < -67.5f) return "下";
        return "右下";
    }

    public static void ClearDamageStats()
    {
        DamageStats.Clear();
    }
    #region 收集整合
    //获得目标key的信息
    private static void GetInfoOKey(StringBuilder builder, int key)
    {
        List<ItemType> Items = Oitems[key];
        if (Items.Count <= 0) return;
        //start
        builder.AppendLine(); builder.Append("{"); builder.Append(key);builder.Append("号阵营场上信息: ");
        if (Towel.AllTowel.TryGetValue(key, out var towel))
        {
            builder.AppendLine(); builder.Append("当前子弹量: ");
            builder.Append(towel.value.ToShortString());
        }
        List<ItemType> toRemove = new();
        foreach (ItemType item in Items)
        {
            builder.AppendLine();builder.Append("(");
            if (item.AppendTo(builder))
                toRemove.Add(item);
            builder.AppendLine();builder.Append(")");
        }
        foreach (var item in toRemove)
        {
            Items.Remove(item);
            GuidToTransform.Remove(item.guid);
        }
        //end
        builder.AppendLine(); builder.Append("}");
    }
    //输出弹珠信息：清理已销毁的，列出存活数量和每个弹珠的值
    private static void GetInfoMarble(StringBuilder builder, int key)
    {
        List<MarbleType> items = MarbleItems[key];
        if (items.Count <= 0) return;
        builder.AppendLine(); builder.Append("{");
        builder.Append(key); builder.Append("号阵营弹珠: ");
        var toRemove = new List<MarbleType>();
        foreach (var item in items)
        {
            builder.AppendLine(); builder.Append("(");
            if (item.AppendTo(builder))
            { }
            else
                toRemove.Add(item);
            builder.AppendLine(); builder.Append(")");
        }
        foreach (var item in toRemove)
        {
            items.Remove(item);
            GuidToTransform.Remove(item.guid);
        }
        builder.AppendLine(); builder.Append("}");
        ReadyActionManager.SetMarbleCount(key, items.Count);   // 预行动条件 marble 用
    }


    private static void GetInfoProp(StringBuilder builder, int stage)
    {
        var props = MapConfig.Instance.teamProps[stage];
        if (props.Count <= 0) return;

        int limit = MapConfig.Instance.propLimit;
        bool full = props.Count >= limit;

        builder.AppendLine(); builder.Append("{");
        builder.Append("当前己方道具栈(上限:"); builder.Append(limit); builder.Append("): ");
        foreach (var p in props)
        {
            builder.AppendLine(); builder.Append("(");
            builder.Append(p.item.ToString()); builder.Append(" "); builder.Append(p.value.ToShortString());
            builder.AppendLine(); builder.Append(")");
        }
        builder.AppendLine(); builder.Append("}");

        // 满槽提醒：空槽才会积累升级值，而且满槽时新道具会溢出被立刻用掉。
        // 这条只在满的时候出现，AI 很容易忽略"留一个空位"这件事，所以直接写进情报。
        if (full)
        {
            builder.AppendLine();
            builder.Append("⚠ 你的道具栏已经满了（" + props.Count + "/" + limit + "）：");
            builder.AppendLine();
            builder.Append("**空槽才会积累升级进度**（升级值只按空槽数量涨），满槽期间你**拿不到任何升级**；");
            builder.AppendLine();
            builder.Append("而且之后拿到的道具会**溢出并立刻被用掉**（用在哪由不得你）。所以至少要留 1 个空位：");
            builder.AppendLine();
            builder.Append("用掉一个不划算的道具，或者用 merge_prop 把两个同种道具并成一格腾空位——");
            builder.AppendLine();
            builder.Append("数值得留着的话就合并，数值小且用不上的就赶紧用掉。");
        }
    }

    #endregion
    public void Awake()
    {
        Oitems = new();
        Iitems = new();
        MarbleItems = new();
        GuidToTransform = new();
        DamageStats = new();
        TurretControlLostReason = new();
        InitialTurretPositions = new();
        ImpactStats = new();
        TurretMoveNotice = new();
    }
}


public class DamageSourceInfo
{
    public int sourceStage;
    public string sourceGuid = "";
    public string sourceDesc = "";
    public HugeInt damage = 0;
}

/// <summary>一次撞击情报：谁撞了谁、撞击点坐标、护盾撞击前后大小。只发给撞击方。
/// 同一目标同一来源、撞击点落在同一个 0.1×0.1 方格（±0.05）内的多次撞击已经合并成一条，count 是合并进这条的次数。</summary>
public class ImpactInfo
{
    public int targetStage;
    public string targetKind = "";
    public Vector2 point;
    public HugeInt shieldBefore = 0;
    public HugeInt shieldAfter = 0;
    public string sourceKind = "";
    /// <summary>合并进来的撞击次数（1 = 只有一次）。</summary>
    public int count = 1;
}

//接下来实际的获取逻辑在InformGeter里，ItemPos不需要任何计算。
public class ItemType //对象类
{
    public string guid = "";//唯一ID
    public string description = "";//解释
    public enum OutputMode { Full, Position, Width, Value , Velocity }
    OutputMode kind = OutputMode.Full;//默认物体区分

    public Transform item = null;
    public IStageValue stageValue = null;
    public Rigidbody2D rb = null;


    bool item_null => string.IsNullOrEmpty(guid) || item == null;
    bool rb_null => string.IsNullOrEmpty(guid) || rb == null;
    bool value_null => string.IsNullOrEmpty(guid) || stageValue == null;
    public Vector2 pos { get { if (item_null) return _pos; else return item.position; } }
    private Vector2 _pos = new();

    public float width { get { if (item_null) return _width; else return item.lossyScale.x; } }
    private float _width = 0;

    public string value { get { if (value_null) return _value; else return stageValue.value.ToShortString(); } }
    private string _value = "0";

    public Vector2 velocity { get { if (rb_null) return _velocity; else return rb.velocity; } }
    private Vector2 _velocity = new();
    public void Clear()
    {
        guid = "";
        description = "";
        kind = OutputMode.Full;
        item = null;
        stageValue = null;
        rb = null;
        _pos = new();
        _width = 0;
        _value = "0";
        _velocity = new();
    }
    public ItemType(OutputMode Ikind, Vector2 Ipos = new(), float Iwidth = 0,string Ivalue = "0" , Vector2 Ivelocity = new())
    {
        kind = Ikind;
        _pos = Ipos;
        _width = Iwidth;
        _value = Ivalue;
        _velocity = Ivelocity;
    }

    public ItemType(Transform Iitem, string Idescription = "",IStageValue IstageValue = null, Rigidbody2D Irb = null)
    {
        //自动生成一个guid
        kind = OutputMode.Full;
        guid = ((uint)Iitem.GetInstanceID()).ToString("x8");
        description = Idescription;
        item = Iitem;
        stageValue = IstageValue;
        rb = Irb;
    }
    /// <summary>
    /// 传入一个stringbuilder
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="mode"></param>
    /// <returns>true 代表 这个被记录物已消失</returns>
    public bool AppendTo(StringBuilder builder, OutputMode? mode = null)
    {
        builder.AppendLine(); builder.Append(description);
        if (!string.IsNullOrEmpty(guid))
        {
            builder.AppendLine(); builder.Append("guid: "); builder.Append(guid);
        }
        OutputMode currentKind = mode ?? kind;

        bool BeDestroyed = false;
        switch (currentKind)
        {
            case OutputMode.Full://默认物体

                if (item_null)
                {
                    builder.AppendLine(); builder.Append("已消失或被摧毁");
                    BeDestroyed = true;
                    break;
                }
                builder.AppendLine(); builder.Append("坐标: "); builder.Append(pos);
                if (width != 0)
                {
                    builder.AppendLine(); builder.Append("直径: "); builder.Append(width);
                }
                if (!value_null)
                {
                    builder.AppendLine(); builder.Append("数值: "); builder.Append(value);
                }
                if (rb_null)
                {
                    builder.AppendLine(); builder.Append("非动态");
                }
                else
                {
                    builder.AppendLine(); builder.Append("动态: "); builder.Append("速度: "); builder.Append(velocity);
                }
                break;
            case OutputMode.Position://只要位置
                builder.AppendLine(); builder.Append("位置: "); builder.Append(pos);
                break;
            case OutputMode.Width://只要直径
                builder.AppendLine(); builder.Append("直径: "); builder.Append(width);
                break;
            case OutputMode.Value://只要数值
                builder.AppendLine(); builder.Append("数值: "); builder.Append(value);
                break;
            case OutputMode.Velocity://只要速度
                builder.AppendLine(); builder.Append("速度: "); builder.Append(velocity);
                break;
            default:
                builder.AppendLine(); builder.Append("属性未知？");
                break;
        }
        return BeDestroyed;
    }
}

//弹珠注册项：独立于ItemType的注册表条目，追踪单个弹珠的引用和值
public class MarbleType
{
    public string guid;
    public int stage;
    public string valueStr;
    public uint valueExponent;
    public Transform item;
    public Rigidbody2D rb;

    public MarbleType(Marble m)
    {
        guid = ((uint)m.gameObject.GetInstanceID()).ToString("x8");
        stage = m.stage;
        valueExponent = m.ValueExponent;
        valueStr = HugeInt.Pow(2, (int)valueExponent).ToShortString();
        item = m.transform;
        rb = m.GetComponent<Rigidbody2D>();
    }

    public bool AliveCheck() => item != null && item.gameObject.activeSelf;

    public bool AppendTo(StringBuilder builder)
    {
        if (!AliveCheck()) return false;
        builder.Append(valueStr);
        return true;
    }
}
