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
    #endregion
    //获得目标stage能获得的全部信息
    public static void GetInfo(StringBuilder builder, int stage)
    {
        //自己的道具栈放最前面，AI 第一眼就能看到
        GetInfoProp(builder, stage);

        //全局可见信息
        foreach (var key in Oitems.Keys)
        {
            GetInfoOKey(builder, key);
        }
        //弹珠为阵营私有信息，只返回请求方自己的
        foreach (var key in MarbleItems.Keys)
        {
            if (key != stage) continue;
            GetInfoMarble(builder, key);
        }
        AppendUpgradeProgress(builder, stage);
        AppendTerritoryArea(builder);
        AppendDamageInfo(builder, stage);
        ClearDamageStats();
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
