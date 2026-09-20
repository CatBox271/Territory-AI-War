using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 地图炫光（挂到任意 GameObject 上）：本体不建任何网格，只把自己登记进 MapGlowLayer。
/// 整层炫光由 MapGlowLayer 用「一个盖住地图的大 quad + 数组传参 + 一张曲线 LUT」一次画完 → 全部炫光 1 个 draw call。
///
/// 剖面形状由 falloff 曲线决定（x = 归一化距离，y = 亮度倍数）：
///   环形：x 0.5 = 正好在环半径上，向左是环内侧、向右是环外侧，两边各映射 softness 个世界单位
///   点状：x 0 = 中心，x 1 = 离中心 softness 个世界单位
/// 曲线在 shader 里没法直接求值，所以 MapGlowLayer 会把它烤成一张 64×32 的 LUT 贴图，一行一个炫光（曲线改了才重烤）。
///
/// 用法：
///   1) 挂到任意物体上（prefab 上直接放一个最省事；同一个物体上可以叠多个，比如一个环形 + 一个点状）
///   2) 代码 MapGlow.Attach(...)（同形状的已有就复用，没有就再挂一个）
///   3) 整体强度 MapGlow.GlobalIntensity
/// </summary>
public class MapGlow : MonoBehaviour
{
    public enum GlowShape
    {
        点状 = 0,
        环形 = 1
    }

    [Header("形状")]
    public GlowShape shape = GlowShape.环形;
    [Tooltip("环半径（世界单位）；点状忽略（点的大小看映射半径和曲线）")]
    public float radius = 1.1f;
    [Tooltip("曲线往环内侧映射多远（世界单位）；点状忽略")]
    public float thickness = 0.66f;
    [Tooltip("曲线往环外侧映射多远（世界单位）；点状 = 从中心往外这么远")]
    public float softness = 0.9f;

    [Header("剖面曲线（x = 归一化距离，y = 亮度倍数）")]
    [Tooltip("环形：0.5 = 环半径处；点状：0 = 中心。留空则按形状给默认剖面")]
    public AnimationCurve falloff = DefaultCurve(GlowShape.环形);

    [Header("颜色与强度")]
    [ColorUsage(true, true)]
    public Color color = Color.white;
    [Range(0f, 500f)]
    public float intensity = 1f;

    [Header("金属感（都是拿背后的地图来调，0 = 关）")]
    [Tooltip("A 地图反射：光环颜色映出它背后的地图（领土什么色就映什么色）")]
    [Range(0f, 1f)] public float metal = 0.45f;
    [Tooltip("D 明暗分层：亮度按地图明暗压，暗处不再无脑糊一层亮")]
    [Range(0f, 1f)] public float shade = 0.35f;
    [Tooltip("B 拉丝条纹强度")]
    [Range(0f, 2f)] public float sheen = 0.3f;
    [Tooltip("B 拉丝条数（绕圆周一圈多少条，取整）")]
    [Range(0f, 64f)] public float sheenCount = 18f;
    [Tooltip("B 拉丝流动速度（正弦相位/秒，0 = 不动）")]
    public float sheenSpeed = 1.5f;
    [Tooltip("C 边缘高光强度：环的内缘 / 外缘各一条细亮边")]
    [Range(0f, 2f)] public float rim = 0.45f;
    [Tooltip("C 边缘高光宽度（占映射半径的比例）")]
    [Range(0.01f, 0.5f)] public float rimWidth = 0.08f;
    [Tooltip("反射采样偏移（世界单位）：越大越像有厚度的高光反射")]
    [Range(0f, 2f)] public float reflectOffset = 0.15f;

    [Header("跟随")]
    [Tooltip("每帧跟着本物体走；关掉就固定在启用时的坐标")]
    public bool follow = true;
    [Tooltip("尺寸跟着本物体的 Transform 缩放一起变（取 lossyScale 的 x 分量）：物体变大时光环一起变大")]
    public bool followScale = false;

    [Header("开关")]
    public bool visible = true;
    [Tooltip("挂上组件就自动显示（Attach 出来的也是这样）")]
    public bool playOnAwake = true;

    // ===== 全局强度：一个参数调所有炫光 =====

    /// <summary>全局强度，1 = 各组件自己的 intensity 原样。所有炫光共用这一个参数。</summary>
    public static float GlobalIntensity
    {
        get => globalIntensity;
        set => globalIntensity = Mathf.Max(0f, value);
    }
    static float globalIntensity = 1f;

    /// <summary>同 GlobalIntensity，写法随意。</summary>
    public static void SetGlobalIntensity(float v) => GlobalIntensity = v;

    /// <summary>场景里登记中的炫光（MapGlowLayer 每帧读它）。</summary>
    internal static readonly List<MapGlow> Active = new List<MapGlow>();

    Vector3 fixedPos;
    bool fixedCaptured;

    /// <summary>这一帧要画的位置（MapGlowLayer 用）。</summary>
    public Vector3 WorldPosition
    {
        get
        {
            if (follow) { fixedCaptured = false; return transform.position; }
            if (!fixedCaptured) { fixedPos = transform.position; fixedCaptured = true; }
            return fixedPos;
        }
    }

    /// <summary>这一帧的尺寸倍率：勾了 followScale 就是本物体的世界缩放（lossyScale.x 的绝对值），否则 1（MapGlowLayer 用）。</summary>
    public float SizeScale
    {
        get
        {
            if (!followScale) return 1f;
            Vector3 s = transform.lossyScale;
            return Mathf.Max(0.0001f, Mathf.Abs(s.x));
        }
    }

    // ===== 默认剖面曲线 =====

    static AnimationCurve ringDefault;
    static AnimationCurve dotDefault;

    static AnimationCurve CachedDefault(GlowShape s)
    {
        if (s == GlowShape.点状)
        {
            if (dotDefault == null)
                dotDefault = new AnimationCurve(
                    new Keyframe(0f, 1f), new Keyframe(0.30f, 0.45f),
                    new Keyframe(0.60f, 0.10f), new Keyframe(1f, 0f));
            return dotDefault;
        }

        if (ringDefault == null)
            ringDefault = new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.30f, 0.06f), new Keyframe(0.5f, 1f),
                new Keyframe(0.70f, 0.10f), new Keyframe(1f, 0f));
        return ringDefault;
    }

    /// <summary>拿一份该形状的默认剖面曲线（新的实例，可以直接编辑/序列化）。</summary>
    public static AnimationCurve DefaultCurve(GlowShape shape) => new AnimationCurve(CachedDefault(shape).keys);

    /// <summary>实际生效的曲线：没填或空曲线时按形状给默认剖面（返回的是只读共享实例，别改）。</summary>
    public AnimationCurve Curve => (falloff == null || falloff.length == 0) ? CachedDefault(shape) : falloff;

    /// <summary>曲线指纹：MapGlowLayer 靠它判断 LUT 里这一行要不要重烤。</summary>
    public int CurveHash()
    {
        var keys = Curve.keys;
        int h = 17;
        h = h * 31 + (int)shape;
        h = h * 31 + keys.Length;
        for (int i = 0; i < keys.Length; i++)
        {
            h = h * 31 + keys[i].time.GetHashCode();
            h = h * 31 + keys[i].value.GetHashCode();
            h = h * 31 + keys[i].inTangent.GetHashCode();
            h = h * 31 + keys[i].outTangent.GetHashCode();
        }
        return h;
    }

    /// <summary>现在的曲线是不是"还没动过的默认剖面"。</summary>
    bool IsDefaultCurve()
    {
        var cur = falloff;
        if (cur == null || cur.length == 0) return true;
        var def = CachedDefault(shape);
        if (cur.length != def.length) return false;
        var a = cur.keys;
        var b = def.keys;
        for (int i = 0; i < a.Length; i++)
        {
            if (Mathf.Abs(a[i].time - b[i].time) > 1e-4f) return false;
            if (Mathf.Abs(a[i].value - b[i].value) > 1e-4f) return false;
        }
        return true;
    }

    // ===== 外部接口 =====

    /// <summary>给任意 GameObject 挂一个炫光：同形状的已有就复用，没有就再挂一个（同一个物体上可以叠多个）。</summary>
    public static MapGlow Attach(GameObject target, GlowShape shape, float radius, Color color, float intensity = 1f)
    {
        if (target == null) return null;
        var glow = Find(target, shape);
        if (glow == null) glow = target.AddComponent<MapGlow>();
        glow.SetShape(shape, radius);
        glow.color = color;
        glow.intensity = Mathf.Max(0f, intensity);
        glow.follow = true;
        glow.playOnAwake = true;
        glow.Show();
        return glow;
    }

    /// <summary>取物体身上的第一个炫光，没有就返回 null。</summary>
    public static MapGlow Get(GameObject target) => target != null ? target.GetComponent<MapGlow>() : null;

    /// <summary>取物体身上第一个指定形状的炫光（同一个物体上可以叠多个）。</summary>
    public static MapGlow Find(GameObject target, GlowShape shape)
    {
        if (target == null) return null;
        var all = target.GetComponents<MapGlow>();
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].shape == shape) return all[i];
        return null;
    }

    public void Show() { visible = true; }
    public void Hide() { visible = false; }
    public void SetVisible(bool v) { visible = v; }
    public void SetColor(Color c) { color = c; }
    public void SetRadius(float r) { radius = Mathf.Max(0f, r); }
    public void SetThickness(float t) { thickness = Mathf.Max(0f, t); }
    public void SetSoftness(float s) { softness = Mathf.Max(0f, s); }
    public void SetIntensity(float i) { intensity = Mathf.Max(0f, i); }

    /// <summary>换形状；曲线还是默认剖面的话，连默认曲线一起换成新形状的。</summary>
    public void SetShape(GlowShape s, float r = -1f)
    {
        if (s != shape && IsDefaultCurve()) falloff = DefaultCurve(s);
        shape = s;
        if (r >= 0f) radius = Mathf.Max(0f, r);
    }

    /// <summary>把剖面曲线恢复成该形状的默认值。</summary>
    public void ResetCurve() { falloff = DefaultCurve(shape); }

    // ===== 登记（不建网格，只进出列表）=====

    void OnEnable()
    {
        if (!Active.Contains(this)) Active.Add(this);
        if (playOnAwake) visible = true;
        fixedCaptured = false;
        MapGlowLayer.Ensure();
    }

    void OnDisable()
    {
        Active.Remove(this);
    }
}
