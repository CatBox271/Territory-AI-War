using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;


public class InformGeter : MonoBehaviour
{
    public MarbleManager MarbleItem;
    public MapConfig MapItem;

    


}
//接下来实际的获取逻辑在InformGeter里，ItemPos不需要任何计算。
public class ItemType //对象类
{
    public string guid = "";//唯一ID
    public string description = "";//解释
    public enum OutputMode { Full, Position, Radius, Velocity }
    OutputMode kind = OutputMode.Full;//默认物体区分

    public Transform item = null;
    public Rigidbody2D rb = null;


    bool item_null => string.IsNullOrEmpty(guid) || item == null;
    bool rb_null => string.IsNullOrEmpty(guid) || rb == null;
    public Vector2 pos { get { if (item_null) return _pos; else return item.position; } }
    private Vector2 _pos = new();

    public float width { get { if (item_null) return _width; else return item.lossyScale.x; } }
    private float _width = 0;

    public Vector2 velocity { get { if (rb_null) return _velocity; else return rb.velocity; } }
    private Vector2 _velocity = new();
    public void Clear()
    {
        guid = "";
        description = "";
        kind = OutputMode.Full;
        item = null;
        rb = null;
        _pos = new();
        _width = 0;
        _velocity = new();
    }
    public ItemType(OutputMode Ikind, Vector2 Ipos = new(), float Iwidth = 0, Vector2 Ivelocity = new())
    {
        kind = Ikind;
        _pos = Ipos;
        _width = Iwidth;
        _velocity = Ivelocity;
    }

    public ItemType(Transform Iitem, string Idescription = "", Rigidbody2D Irb = null)
    {
        //自动生成一个guid
        kind = OutputMode.Full;
        guid = System.Guid.NewGuid().ToString("N");
        description = Idescription;
        item = Iitem;
        rb = Irb;
    }
    public void AppendTo(StringBuilder builder, OutputMode? mode = null)
    {
        builder.AppendLine(); builder.Append(description);
        if (!string.IsNullOrEmpty(guid))
        {
            builder.AppendLine(); builder.Append("guid: "); builder.Append(guid);
        }
        OutputMode currentKind = mode ?? kind;
        switch (currentKind)
        {
            case OutputMode.Full://默认物体

                if (item_null)
                {
                    builder.AppendLine(); builder.Append("已消失或被摧毁");
                    break;
                }
                builder.AppendLine(); builder.Append("坐标: "); builder.Append(pos);
                if (width != 0)
                {
                    builder.AppendLine(); builder.Append("直径: "); builder.Append(width);
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
            case OutputMode.Radius://只要半径
                builder.AppendLine(); builder.Append("半径: "); builder.Append(width);
                break;
            case OutputMode.Velocity://只要速度
                builder.AppendLine(); builder.Append("速度: "); builder.Append(velocity);
                break;
            default:
                builder.AppendLine(); builder.Append("属性未知？");
                break;
        }
    }
}
