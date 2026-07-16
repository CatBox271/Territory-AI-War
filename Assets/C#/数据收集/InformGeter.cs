using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;


public class InformGeter : MonoBehaviour
{
    public MarbleManager MarbleItem;
    public MapConfig MapItem;
    //接下来数据收集的方法
    //增量收集
    public static Dictionary<int, List<ItemType>> Oitems = new();//外在数据
    public static Dictionary<int, List<ItemType>> Iitems = new();//内部数据，目前没用

    //帮我完成对Marble的注册表式收集

    //有两种调用  1.初始化的调用√  2.新增加的大球的调用√
    public static void AddItem(int stage, ItemType item, bool Out = true)
    {
        if (Out)
        {
            if (!Oitems.ContainsKey(stage)) Oitems[stage] = new();
            Oitems[stage].Add(item);
        }
    }
    //获得目标stage能获得的全部信息
    public static void GetInfo(StringBuilder builder, int stage)
    {
        //全局可见信息
        foreach (var key in Oitems.Keys)
        {
            GetInfoOKey(builder, key);
        }
        
    }
    //获得目标key的信息
    private static void GetInfoOKey(StringBuilder builder, int key)
    {
        List<ItemType> Items = Oitems[key];
        if (Items.Count <= 0) return;
        //start
        builder.AppendLine(); builder.Append("{"); builder.Append(key);builder.Append("号阵营场上信息: ");
        List<ItemType> toRemove = new();
        foreach (ItemType item in Items)
        {
            builder.AppendLine();builder.Append("(");
            if (item.AppendTo(builder))
                toRemove.Add(item);
            builder.AppendLine();builder.Append(")");
        }
        foreach (var item in toRemove)
            Items.Remove(item);
        //end
        builder.AppendLine(); builder.Append("}");
    }
    public void Awake()
    {
        Oitems = new();
    }
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
        guid = System.Guid.NewGuid().ToString("N");
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
