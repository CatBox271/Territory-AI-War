using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WallEditor : MonoBehaviour
{
    public GameObject wallOb;
    [Header("内部大小")]
    public float width = 10;
    public float heigth = 10;
    [Header("墙体宽度（x=上 y=右 z=下 w=左，按 CSS margin 顺序）")]
    public Vector4 wall_width = new Vector4(0.1f, 0.1f, 0.1f, 0.1f);
    [Header("颜色")]
    public Color wall_color = new Color(0.63521993f, 0.63521993f, 0.63521993f, 1f);
    public Color background_color = new Color(0.63521993f, 0.63521993f, 0.63521993f, 1f);
    [Header("渲染顺序")]
    public int wall_sortingOrder = 2;
    public int background_sortingOrder = 1;
    [Header("刷新")]
    public bool refresh;
    public Transform Background;
    public Transform Left;
    public Transform Right;
    public Transform Up;
    public Transform Down;
    private void OnValidate()
    {
        EnsureWalls();
        Adjust();
        refresh = false;
    }

    /// <summary>BackGround/Left/Right/Up/Down 里缺哪个（引用为空，或指向的对象被删了），就用 wallOb(MonoWall) 在自身下面补一个。</summary>
    private void EnsureWalls()
    {
        Background = EnsureWall(Background, "BackGround");
        Up = EnsureWall(Up, "Up");
        Down = EnsureWall(Down, "Down");
        Left = EnsureWall(Left, "Left");
        Right = EnsureWall(Right, "Right");
    }

    private Transform EnsureWall(Transform current, string wallName)
    {
        // 引用还在就沿用（对象被删除时 Unity 的 != null 判断会成立，所以这里能自动补回）
        if (current != null) return current;

        // 引用空了、但同级里已经有同名子物体（只是引用被清空）：先捡回来，避免重复生成
        Transform existing = transform.Find(wallName);
        if (existing != null) return existing;

        if (wallOb == null) return null;   // 没给墙体对象就只跳过，不报错

        GameObject ob = Instantiate(wallOb, transform);
        ob.name = wallName;
        return ob.transform;
    }

    private void Adjust()
    {
        Vector4 m = wall_width;   // m.x=上、m.y=右、m.z=下、m.w=左
        Vector3 self = transform.position;

        // 背景：正好填满内部内容框（不含 margin），颜色与渲染顺序独立可调
        if (Background != null)
        {
            Background.position = self;
            Background.localScale = new Vector3(width, heigth);
            ApplyColorAndOrder(Background, background_color, background_sortingOrder);
        }

        // 先算外框（内容 + 四边 margin），再把每条墙贴到外框对应的边上、并在外框里居中：
        // 左右/上下厚度不对称时，外框中心会相对内容中心偏移，这条偏移必须加上，否则拐角对不上。
        float outerW = width + m.w + m.y;
        float outerH = heigth + m.x + m.z;
        float cx = (m.y - m.w) / 2f;
        float cy = (m.x - m.z) / 2f;

        if (Up != null)
        {
            Up.position = self + new Vector3(cx, heigth / 2f + m.x / 2f);
            Up.localScale = new Vector3(outerW, m.x);
            ApplyColorAndOrder(Up, wall_color, wall_sortingOrder);
        }
        if (Down != null)
        {
            Down.position = self + new Vector3(cx, -(heigth / 2f + m.z / 2f));
            Down.localScale = new Vector3(outerW, m.z);
            ApplyColorAndOrder(Down, wall_color, wall_sortingOrder);
        }
        if (Left != null)
        {
            Left.position = self + new Vector3(-(width / 2f + m.w / 2f), cy);
            Left.localScale = new Vector3(m.w, outerH);
            ApplyColorAndOrder(Left, wall_color, wall_sortingOrder);
        }
        if (Right != null)
        {
            Right.position = self + new Vector3(width / 2f + m.y / 2f, cy);
            Right.localScale = new Vector3(m.y, outerH);
            ApplyColorAndOrder(Right, wall_color, wall_sortingOrder);
        }
    }

    /// <summary>设置颜色与渲染顺序：SpriteRenderer 直接设；其它 Renderer 只设顺序、颜色走 MaterialPropertyBlock（避免在编辑器里实例化材质）。</summary>
    private static void ApplyColorAndOrder(Transform target, Color color, int sortingOrder)
    {
        if (target == null) return;

        SpriteRenderer sr = target.GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.sortingOrder = sortingOrder;
            sr.color = color;
            return;
        }

        Renderer r = target.GetComponent<Renderer>();
        if (r == null) return;

        r.sortingOrder = sortingOrder;
        var mpb = new MaterialPropertyBlock();
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_Color", color);
        mpb.SetColor("_BaseColor", color);
        r.SetPropertyBlock(mpb);
    }
}
