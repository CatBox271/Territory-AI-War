using UnityEngine;
using TMPro;

/// <summary>
/// 叙事舞台的「文本框」：一个 Text.prefab 实例 = 一块圆角容器（ContainerMesh）+ 一段可逐字入场的 TMP 台词。
/// 只负责把「这一块长什么样」写进 prefab 上的部件，不管排版；排版由 StoryTeller 统一做。
///
/// 引用解析顺序（与 SceneSpriteDisplay 一致）：自身 → 子物体（含未激活）→ 父物体，
/// 所以脚本挂在 prefab 根上还是挂在容器上都行。
/// 透明度走 ContainerMesh.SetAlpha / TMP color，不重建网格，演出里每帧调也安全。
///
/// 字号单位：这里是 TMP 的「点」，而这个 prefab 是 3D TextMeshPro —— TMP 对非正交文本会乘
/// orthographicMultiplier 0.1，字体 asset 的 pointSize 又是 90，所以 **1 点 = 0.1 世界单位**。
/// 想让某行占 h 个世界单位高，就用 StageStyle.FontSize(h) 换算，别直接写点数。
/// </summary>
[ExecuteAlways]
public class SceneTextDisplay : MonoBehaviour
{
    [Header("部件（留空自动找回）")]
    public ContainerMesh container;
    public TextMeshPro text;
    public FieldArrivalModifier anim;

    [Header("尺寸（世界单位：容器内孔 = 文字排版区）")]
    public Vector2 size = new Vector2(5f, 5f);

    [Header("外观")]
    public Color containerColor = new Color(0.21960784f, 0.21960784f, 0.21960784f, 1f);
    public Color wallColor = Color.white;
    public Color textColor = Color.white;
    public float cornerRadius = 0.5f;
    public float wallWidth = 0.08f;

    [Header("文案")]
    [TextArea] public string content = "";
    public float fontSize = 30f;

    [Header("排版")]
    [Tooltip("文字对齐：正文卡用 TopLeft（左对齐 + 顶对齐），标题/标签用 Center")]
    public TextAlignmentOptions alignment = TextAlignmentOptions.Center;
    [Tooltip("文字四周内边距（世界单位）：文字区 = 容器内孔 − 2×padding，文字就不会贴着描边")]
    public float textPadding = 0f;
    [Tooltip("逐字入场整段最多播多久（秒）：字多时自动加快打字速度，免得一段长思考打十几秒")]
    public float typeMaxSeconds = 3f;
    [Tooltip("文字装不下时：Overflow = 溢出框外（标题/标签用）；Ellipsis = 裁到装得下并加省略号（正文卡用，免得压到别的卡上）")]
    public TextOverflowModes overflow = TextOverflowModes.Overflow;

    [Header("渲染顺序")]
    [Tooltip("容器与文字的排序号；文字自动 = 它 +2，保证底板压不住字")]
    public int sortingOrder = 1;

    [Header("字体材质")]
    [Tooltip("留空 = 用 prefab 自带的那份字体材质")]
    public Material textMaterial;

    [Header("描边")]
    [Tooltip("描边宽度（0 = 不描边）。>0 时基于字体材质建一份**逐对象实例**，只影响这一段文字")]
    public float outlineWidth = 0f;
    public Color outlineColor = Color.black;
    [Tooltip("字面膨胀：描边时加一点字更实（0 = 不动）")]
    public float faceDilate = 0f;

    [Header("刷新")]
    public bool refresh;

    private float currentAlpha = 1f;

    public ContainerMesh Container => container != null ? container : (container = Resolve<ContainerMesh>());
    public TextMeshPro Text => text != null ? text : (text = Resolve<TextMeshPro>());
    public FieldArrivalModifier Anim => anim != null ? anim : (anim = Resolve<FieldArrivalModifier>());

    private T Resolve<T>() where T : Component
    {
        T found = GetComponent<T>();
        if (found != null) return found;
        found = GetComponentInChildren<T>(true);
        if (found != null) return found;
        return GetComponentInParent<T>(true);
    }

    // 只在真实场景里写值：Project 里的 prefab 资产自身不动，避免演出参数污染模板
    private void OnEnable() { if (gameObject.scene.IsValid()) Apply(); }
    private void OnValidate() { if (gameObject.scene.IsValid()) Apply(); }

    [ContextMenu("应用")]
    public void Apply()
    {
        refresh = false;
        ApplyAppearance();
        ApplyContent();
    }

    /// <summary>写尺寸与配色（不动文案）。</summary>
    public void ApplyAppearance()
    {
        ContainerMesh c = Container;
        if (c != null)
        {
            c.width = size.x;
            c.heigth = size.y;
            c.corner_radius = Vector4.one * cornerRadius;
            c.wall_width = Vector4.one * wallWidth;
            c.wall_color = wallColor;
            c.background_color = containerColor;
            c.sortingOrder = sortingOrder;
            c.alpha = currentAlpha;   // 重建后把当前透明度接回去
            c.Rebuild();
        }

        TextMeshPro t = Text;
        if (t != null)
        {
            // 排版区 = 容器内孔 − 内边距。不写 RectTransform 的话 TMP 会一直按 prefab 里的 5×5 折行：
            // 12 单位宽的标题会在第 5 个单位处断行，屏上看起来像竖排。
            RectTransform rt = t.rectTransform;
            if (rt != null)
            {
                float pad = Mathf.Max(0f, textPadding);
                rt.sizeDelta = new Vector2(
                    Mathf.Max(size.x - pad * 2f, 0.01f),
                    Mathf.Max(size.y - pad * 2f, 0.01f));
            }

            t.alignment = alignment;

            // 材质：指定了 textMaterial 就用它；要描边就基于它（或当前材质）建一份逐对象实例
            Material baseMat = textMaterial != null ? textMaterial : t.fontSharedMaterial;
            if ((outlineWidth > 0f || faceDilate != 0f) && baseMat != null)
            {
                if (_outlineMaterial == null || _outlineSource != baseMat)
                {
                    ReleaseOutlineMaterial();
                    _outlineMaterial = new Material(baseMat)
                    {
                        hideFlags = HideFlags.DontSave,
                        name = baseMat.name + " (Outline)"
                    };
                    _outlineSource = baseMat;
                }
                _outlineMaterial.SetFloat("_OutlineWidth", Mathf.Max(0f, outlineWidth));
                _outlineMaterial.SetColor("_OutlineColor", outlineColor);
                _outlineMaterial.SetFloat("_FaceDilate", faceDilate);
                t.fontSharedMaterial = _outlineMaterial;
            }
            else
            {
                ReleaseOutlineMaterial();
                if (baseMat != null && t.fontSharedMaterial != baseMat) t.fontSharedMaterial = baseMat;
            }

            t.wordWrappingRatios = 0f;   // 满行才换行：中文别因为「一个词放不下」就整词提前折行
            if (fontSize > 0f) t.fontSize = fontSize;
            t.textWrappingMode = TextWrappingModes.Normal;   // 按框宽折行
            t.overflowMode = overflow;                       // 正文卡裁到装得下，标题/标签允许溢出
            t.sortingOrder = sortingOrder + 2;   // 文字永远压在容器之上
            Color col = textColor;
            col.a = textColor.a * currentAlpha;
            t.color = col;
        }

        FieldArrivalModifier f = Anim;
        if (f != null)
        {
            f.playOnEnable = false;   // 入场时机由舞台控制
            f.fadable = false;        // 停在舞台上，不用它自己淡出
            f.useStageClock = true;   // 逐字入场走舞台时钟（和 WaitForCaptureUpdate 同一个钟），速度与演出其余部分一致
        }
    }

    /// <summary>写文案与字号；改完让 TMP 立刻重排，入场动画才有顶点可缓存。</summary>
    public void ApplyContent()
    {
        TextMeshPro t = Text;
        if (t == null) return;
        if (fontSize > 0f) t.fontSize = fontSize;
        t.text = content ?? "";
        t.ForceMeshUpdate();
    }

    /// <summary>整体透明度（容器 + 文字）：容器走 SetAlpha（不重建网格），文字走 TMP color。</summary>
    public void SetAlpha(float a)
    {
        a = Mathf.Clamp01(a);
        if (Mathf.Abs(currentAlpha - a) < 0.001f) return;
        currentAlpha = a;

        Container?.SetAlpha(a);

        TextMeshPro t = Text;
        if (t != null)
        {
            Color col = textColor;
            col.a = textColor.a * a;
            t.color = col;
        }
    }

    /// <summary>逐字入场（走 FieldArrivalModifier：每个字从中心带随机旋转弹出来）。</summary>
    public void PlayIn()
    {
        FieldArrivalModifier f = Anim;
        if (f == null) return;
        f.playOnEnable = false;
        f.fadable = false;
        f.Stop();

        // 字多的时候自动加快：整段打字时间封顶 typeMaxSeconds，不然 90 个字的思考要打 9 秒
        string body = content ?? "";
        int len = body.Length;
        if (len > 1 && typeMaxSeconds > 0f)
        {
            float interval = (typeMaxSeconds - f.charDuration) / (len - 1);
            f.charInterval = Mathf.Clamp(interval, 0.02f, 0.1f);
        }

        f.PlayWithText(body);
        f.ApplyEnterNow();   // 立刻落到 t=0，别让整段字先亮一帧再跳回起点
    }

    /// <summary>停掉逐字动画，直接显示全部文字。</summary>
    public void StopIn()
    {
        if (Anim != null) Anim.Stop();
    }

    private Material _outlineMaterial;
    private Material _outlineSource;

    /// <summary>释放描边用的材质实例（切到不需要描边、或对象销毁时）。</summary>
    private void ReleaseOutlineMaterial()
    {
        if (_outlineMaterial == null) return;
        if (Application.isPlaying) Destroy(_outlineMaterial);
        else DestroyImmediate(_outlineMaterial);
        _outlineMaterial = null;
        _outlineSource = null;
    }

    private void OnDestroy()
    {
        ReleaseOutlineMaterial();
    }
}
