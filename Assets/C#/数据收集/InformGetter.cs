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

    // 每个 AI 最近的公开 content（AIAgent.ReceiveResponse 写入），附加到全局信息里给所有 AI 看。
    public static Dictionary<int, string> AIContents = new();

    public static void SetAIContent(int stage, string content)
    {
        if (stage < 0 || string.IsNullOrWhiteSpace(content)) return;
        AIContents[stage] = content.Trim();
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
        //自己的道具栈放最前面，AI 第一眼就能看到
        GetInfoProp(builder, stage);
        AppendTurretControlNotice(builder, stage);
        AppendNearestEnemyTerritory(builder, stage);
        TurretControlLostReason.Remove(stage);

        //全局可见信息
        foreach (var key in Oitems.Keys)
        {
            GetInfoOKey(builder, key);
        }
        AppendBallImpactWarning(builder, stage);
        AppendAIContents(builder);
        //弹珠为阵营私有信息，只返回请求方自己的
        foreach (var key in MarbleItems.Keys)
        {
            if (key != stage) continue;
            GetInfoMarble(builder, key);
        }
        AppendUpgradeProgress(builder, stage);
        AppendTerritoryArea(builder);
        AppendDamageInfo(builder, stage);
        if (clearDamage) ClearDamageStats();
        builder.AppendLine(); builder.AppendLine(IntelInfoEnd);
    }

    /// <summary>把每个 AI 的公开 content 追加到全局信息（所有阵营都可见）。</summary>
    private static void AppendAIContents(StringBuilder builder)
    {
        bool has = false;
        for (int s = 1; s <= 4; s++)
        {
            if (AIContents.TryGetValue(s, out string c) && !string.IsNullOrWhiteSpace(c)) { has = true; break; }
        }
        if (!has) return;

        builder.AppendLine(); builder.AppendLine("各AI本轮发言:");
        for (int s = 1; s <= 4; s++)
        {
            if (AIContents.TryGetValue(s, out string c) && !string.IsNullOrWhiteSpace(c))
                builder.AppendLine($"{AIAgent.GetStageName(s)}：{c}");
        }
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
        }
    }
    #endregion


    /// <summary>把请求方自己受到的伤害统计拼进 AI 上下文（阵营私有）。</summary>

    /// <summary>炮塔控制自动断开提示：每个阵营每次 GetInfo 最多输出一次。</summary>
    private static void AppendTurretControlNotice(StringBuilder builder, int stage)
    {
        if (!TurretControlLostReason.TryGetValue(stage, out string reason) || string.IsNullOrWhiteSpace(reason)) return;

        builder.AppendLine(); builder.AppendLine($"炮塔控制已断开：{reason}。如需继续瞄准，请重新调用 control_turret start。");
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

    #region 大球撞击预警
    // 轨迹推演参数：最多预测 12 秒、步长 0.02 秒。“几秒后第一次撞上护盾”按近似计算。
    private const float BallWarningMaxPredictTime = 12f;
    private const float BallWarningStep = 0.02f;

    /// <summary>
    /// 大球撞击预警：对每个敌方大球做轨迹推演（直线运动 + 地图边界反弹 + 敌方护盾镜面反弹；
    /// 同队护盾与球物理无碰撞，跳过），计算它大约几秒后第一次撞上请求方护盾。
    /// 只在预测窗口内会撞上的大球输出预警，格式仿照“最近敌方领土”块。
    /// </summary>
    private static void AppendBallImpactWarning(StringBuilder builder, int stage)
    {
        var config = MapConfig.Instance;
        var canvas = TerritoryCanvas.Instance;
        if (config == null || canvas == null) return;
        if (!Towel.AllTowel.TryGetValue(stage, out Towel self) || self == null || self.shield_value <= 0) return;

        Vector2 mapCenter = canvas.transform.position;
        float mapHalf = config.worldSize * 0.5f;
        Vector2 selfShieldPos = self.transform.position;
        float selfShieldR = self.shield != null ? self.shield.transform.lossyScale.x : 0f;
        if (selfShieldR <= 0f) return;

        foreach (var kv in Oitems)
        {
            if (kv.Key == stage) continue;

            foreach (ItemType item in kv.Value)
            {
                if (item.description != "大球" || item.item == null) continue;

                BallPainter bp = item.stageValue as BallPainter;
                float ballR = bp != null
                    ? Mathf.Max(item.item.lossyScale.x, item.item.lossyScale.y) * bp.baseWorldRadius
                    : 0f;
                float impactT = PredictShieldImpact(
                    item.item.position,
                    item.rb != null ? item.rb.velocity : Vector2.zero,
                    ballR, mapCenter, mapHalf, stage, selfShieldPos, selfShieldR);
                if (impactT < 0f) continue;

                builder.AppendLine(); builder.Append("{");
                builder.Append("大球撞击预警: "); builder.Append(AIAgent.GetStageName(kv.Key));
                builder.Append("的大球(");
                builder.Append("guid: "); builder.Append(item.guid);
                builder.Append(", 数值 "); builder.Append(item.value);
                builder.Append(", 直径 "); builder.Append((ballR * 2).ToString("0.0"));
                builder.Append(")预计 "); builder.Append(impactT.ToString("0.0"));
                builder.Append(" 秒后第一次撞上你的护盾(护盾当前直径 ");
                builder.Append((selfShieldR * 2).ToString("0.0")); builder.Append(")");
                builder.AppendLine(); builder.Append("}");
            }
        }
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
        if (dist <= EnemyTerritoryDangerDistance) builder.Append("【危险：距离3】");
        builder.Append("，相对方向 "); builder.Append(dirText);
        builder.AppendLine();
        builder.AppendLine("(附近存在敌方领土时，敌方的子弹会长驱直入！炮塔有子弹时会自动处理,所以不要控制炮塔去处理这个事情啦，不过大球，霰弹等有指向性的道具还是可以用来消灭敌方领土的。如想要弄死对方请使用对方炮塔的坐标而不是这个)");
        builder.Append("}");
    }

    private static string CardinalDirection(Vector2 dir)
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
    }


    private static void GetInfoProp(StringBuilder builder, int stage)
    {
        var props = MapConfig.Instance.teamProps[stage];
        if (props.Count <= 0) return;
        builder.AppendLine(); builder.Append("{");
        builder.Append("当前己方道具栈(上限:"); builder.Append(MapConfig.Instance.propLimit); builder.Append("): ");
        foreach (var p in props)
        {
            builder.AppendLine(); builder.Append("(");
            builder.Append(p.item.ToString()); builder.Append(" "); builder.Append(p.value.ToShortString());
            builder.AppendLine(); builder.Append(")");
        }
        builder.AppendLine(); builder.Append("}");
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
    }
}


public class DamageSourceInfo
{
    public int sourceStage;
    public string sourceGuid = "";
    public string sourceDesc = "";
    public HugeInt damage = 0;
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
