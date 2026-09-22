using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public enum SpriteEmotion
{
    origin,
    smile,
    laugh,
    shock,
    angry,
    sad,
    win,
    fail,
}

public class UISprite : MonoBehaviour
{
    public SpriteRenderer sp;
    public enum XR
    { 
        None,
        Left,
        Right,
    }
    public enum YR
    {
        None,
        Top,
        Botton,
    }
    public Animation anim;

    public XR x_relative;
    public YR y_relative;

    public float x_relative_value;
    public float y_relative_value;

    public float Scale = 1.7f;
    public float additive_scale = 0f;
    public Vector3 additive_pos = new();
    private Vector3 last_additive_pos = new();

    [Header("网点（dot）：立绘整体透明走网点材质，不用半透明")]
    [Tooltip("0 = 不动材质（原来的普通 sprite）。>0 = 立绘改用网点材质渲染，「整体可见度」= 这个值：" +
             "1 = 原图；0.25 = 网点很稀、整张图只留约四分之一可见（越小越透）。")]
    [Range(0f, 1f)] public float dotVisibility = 0.25f;

    [Tooltip("网点材质：留空时先看 SpriteRenderer 上已经挂的是不是网点材质，" +
             "不是就按 Resources 里的 Material/立绘Dot → Material/SpriteMeshDot 顺序找")]
    public Material dotMaterial;

    /// <summary>网点 shader（和舞台立绘 / 图片卡同一套）。</summary>
    public const string DotShaderName = "Custom/SpriteMeshDot";
    private const string DotMaterialResource = "Material/立绘Dot";
    private const string DotMaterialFallback = "Material/SpriteMeshDot";

    private Material dotInstance;     // 每个立绘一份实例：改网点参数不会串到别人身上
    private Material dotSource;       // 实例是从哪份材质拷的
    private Sprite dotSprite;         // 上次同步用的 sprite（换表情要重算 _SpriteRect/_ContentRect）
    private float dotAlpha = -1f;     // 上次同步的透明度（出现/消失动画在改 SpriteRenderer 的 m_Color.a）
    private bool dotWarned;

    private Vector3 aim_pos;
    private Vector3 delta_pos;

    private void Awake()
    {
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        ApplyDot();
    }

    private void OnValidate()
    {
        Adjust();
    }

    public void Adjust()
    {
        aim_pos = new(
            x_relative switch{
                XR.Left => -(x_relative_value + additive_pos.x),
                XR.Right => (x_relative_value + additive_pos.x),
                _ => 0,
            },
            y_relative switch{
                YR.Top => (y_relative_value + additive_pos.y),
                YR.Botton => -(y_relative_value + additive_pos.y),
                _ => 0,
            });
        transform.localPosition = delta_pos + aim_pos;
        transform.localScale = Vector3.one * (Scale + additive_scale);
    }
    public bool show;
    public void Set(int stage, SpriteEmotion emo)
    {
        try
        {
            sp.sprite = (Resources.Load(StoryTeller.PortraitFolder + AIAgent.GetStageName(stage) + "_" + emo.ToString())
                         ?? Resources.Load(StoryTeller.PortraitFolder + AIAgent.GetStageName(stage) + "_origin")) as Sprite;
            if (AIAgent.Instance != null)
            {
                var card = AIAgent.Instance.cards.Find(c => c.position == stage);
                if (card != null)
                {
                    if (card.x_relative != XR.None)
                    {
                        x_relative = card.x_relative;
                        x_relative_value = card.RelativePos.x;
                    }
                    if (card.y_relative != YR.None)
                    {
                        y_relative = card.y_relative;
                        y_relative_value = card.RelativePos.y;
                    }
                    if (Mathf.Abs(card.Scale - 1.7f) > 0.0001f)
                        Scale = card.Scale;
                    Adjust();
                }
            }
        }
        catch
        { }
        SyncDot();      // 换了表情就是换了张贴图，网点参数要跟着重算
    }
    public void Act(bool show)
    {
        if (show) anim.Play("SpriteDuration");
        else anim.Play("SpriteFade");
        this.show = show;
    }
    private void Update()
    {
        if (last_additive_pos != additive_pos)
        {
            Adjust();
            last_additive_pos = additive_pos;
        }
        SyncDot();
    }

    // ---------------- 网点（dot）整体透明 ----------------

    /// <summary>
    /// 把立绘换成网点（dot）材质，「整体可见度」= dotVisibility（默认 0.25）。
    ///
    /// 为什么不能只把材质拖上去就完事：网点 shader 是给 Mesh 图片卡写的 —— 它**不看网格自带的 UV**，
    /// 而是拿「本地坐标 + _ContentRect / _SpriteRect」自己算采样点。直接挂到 SpriteRenderer 上，
    /// 这两个值还是材质里的默认值（±0.5），立绘就只剩中间一小块。
    /// 所以这里按当前 sprite 把 _SpriteRect（图在贴图里的 uv 矩形）和 _ContentRect（网格本地范围）补齐；
    /// 立绘的图是紧贴轮廓的 Tight 网格，本地坐标仍然是按整张 sprite 的 rect 算的，和 SceneSpriteDisplay 一个口径。
    ///
    /// 出现/消失是 Animation 在改 SpriteRenderer 的 m_Color.a，而网点 shader 不乘顶点色，
    /// 所以 SyncDot 每帧把那个 alpha 镜像到材质的 _Color.a 上，淡入淡出照旧。
    /// </summary>
    public void ApplyDot()
    {
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        if (sp == null) return;

        if (dotVisibility <= 0f)
        {
            if (dotInstance != null)
            {
                if (dotSource != null) sp.sharedMaterial = dotSource;   // 关掉就还原成原来那份
                DestroyMaterial(dotInstance);
                dotInstance = null;
                dotSource = null;
            }
            return;
        }

        Material src = dotMaterial;
        if (src == null && IsDotMaterial(sp.sharedMaterial) && sp.sharedMaterial != dotInstance)
            src = sp.sharedMaterial;            // 预制体 / 场景里已经挂了网点材质：优先用挂上去的那份
        if (src == null) src = Resources.Load<Material>(DotMaterialResource);
        if (src == null) src = Resources.Load<Material>(DotMaterialFallback);
        if (src == null)
        {
            if (!dotWarned)
            {
                dotWarned = true;
                Debug.LogWarning($"[UISprite] 找不到网点材质（Resources：{DotMaterialResource} / {DotMaterialFallback}），立绘保持原样。");
            }
            return;
        }

        if (dotInstance == null || dotSource != src)
        {
            if (dotInstance != null) DestroyMaterial(dotInstance);
            dotSource = src;
            dotInstance = new Material(src) { name = src.name + "（立绘实例）" };
            dotSprite = null;                   // 换材质要重新算一次内容区
        }

        dotInstance.SetFloat("_Alpha", Mathf.Clamp01(dotVisibility));
        // 网点大小 / 疏密用的是材质里的 _DotDensity（每本地单位多少格），想改网点粗细就改材质，代码不覆盖
        if (sp.sharedMaterial != dotInstance) sp.sharedMaterial = dotInstance;
        SyncDot();
    }

    /// <summary>把当前 sprite 的 uv 矩形 / 本地范围、以及动画改出来的透明度同步进网点材质。每帧都调，开销很小。</summary>
    private void SyncDot()
    {
        if (dotInstance == null || sp == null) return;

        Sprite s = sp.sprite;
        if (s != dotSprite)
        {
            dotSprite = s;
            dotInstance.SetVector("_SpriteRect", SceneSpriteDisplay.GetSpriteUvRect(s));
            dotInstance.SetVector("_ContentRect", ContentRectOf(s));
        }

        float a = sp.color.a;                  // 出现/消失动画改的是 SpriteRenderer 的颜色 alpha
        if (!Mathf.Approximately(a, dotAlpha))
        {
            dotAlpha = a;
            dotInstance.SetColor("_Color", new Color(1f, 1f, 1f, a));
        }
    }

    /// <summary>
    /// sprite 的网格本地范围（minX, minY, maxX, maxY）：按**整张 rect**算、以 pivot 为原点，
    /// 和 SpriteRenderer 生成的网格坐标同一个口径（Tight 网格只是把空白处挖掉，坐标空间不变）。
    /// </summary>
    private static Vector4 ContentRectOf(Sprite s)
    {
        if (s == null) return new Vector4(-0.5f, -0.5f, 0.5f, 0.5f);
        Rect r = s.rect;
        float ppu = s.pixelsPerUnit > 0f ? s.pixelsPerUnit : 100f;
        // sprite.pivot：大多数图是「相对 rect」的像素坐标；rect 不在原点时个别资源给的是整张贴图坐标，减掉偏移
        Vector2 pivot = s.pivot;
        if (pivot.x > r.width || pivot.y > r.height) pivot = new Vector2(pivot.x - r.xMin, pivot.y - r.yMin);
        if (pivot.x < 0f || pivot.x > r.width) pivot.x = r.width * 0.5f;
        if (pivot.y < 0f || pivot.y > r.height) pivot.y = r.height * 0.5f;
        return new Vector4(
            -pivot.x / ppu,
            -pivot.y / ppu,
            (r.width - pivot.x) / ppu,
            (r.height - pivot.y) / ppu);
    }

    private static bool IsDotMaterial(Material m)
    {
        return m != null && m.shader != null && m.shader.name == DotShaderName;
    }

    private static void DestroyMaterial(Material m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m);
        else DestroyImmediate(m);
    }

    private void OnDestroy()
    {
        DestroyMaterial(dotInstance);
        dotInstance = null;
        dotSource = null;
    }
}
