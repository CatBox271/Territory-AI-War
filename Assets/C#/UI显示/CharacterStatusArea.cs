using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 角色状态区：实时显示某个阵营 AI 的【我说】+ 立绘。**每个阵营一块**（4 块就放 4 个实例，各填自己的 stage）。
///
/// 立绘走舞台那套：SceneSpriteDisplay（Mesh 网格 + 网点材质 Custom/SpriteMeshDot）。
/// 它本来"看图永远铺满内容区、网格永远 ≥ 图"，所以结构上切不断；这里靠给 SpriteMeshDot 新加的
/// **可选裁切矩形 _ClipRect**（默认空矩形 = 不裁，舞台零影响）把超出面板的部分硬切掉：
///   · 立绘按"原图世界尺寸 × portraitScale"铺开（整幅图都在网格里）
///   · _ClipRect 写成**面板矩形**（网格本地坐标）→ 面板外的一律不显示
///   · portraitOffset 平移立绘，同时把位移从裁切矩形里减掉 → **窗口钉在面板上、动的是人物**
///
/// 参数是**实时**的：Update 里只做几次比较，portraitOffset / portraitScale / portraitSortingOrder /
/// clipSize 一改（Inspector 滑条或代码赋值）下一帧就生效，不用等下一次发言。
///
/// 数据来源：AIAgent.Say(position, content) 里直接推（Push），和消息条 UIMessageBar 走同一个出口，
/// 立绘表情用 [emo:xxx]（AIAgent.ExtractEmotion），取不到的图退回 _origin。
/// </summary>
public class CharacterStatusArea : MonoBehaviour
{
    [Header("身份")]
    [Tooltip("显示哪个阵营（= AI 的 stage / 阵营序号，1 起）。每块状态区填一个不同的值。")]
    public int stage = 1;

    [Header("引用")]
    [Tooltip("【我说】的文本（TMP）")]
    public FieldArrivalModifier speech;//这里用 FieldArrivalModifier做弹字
    public FieldArrivalModifier thinking;//瞬间显示
    [Header("立绘动作")]
    public Animator anim;
    [Tooltip("立绘：舞台那套 SceneSpriteDisplay（MeshRenderer + 网点材质）")]
    public SceneSpriteDisplay portrait;
    [Tooltip("状态区面板的 SpriteRenderer（留空自动取自己身上的）；裁切矩形默认按它的尺寸算")]
    public SpriteRenderer panel;

    [Header("阵营配色")]
    [Tooltip("跟着阵营变色的 SpriteRenderer（留空 = 自动找子物体里叫「底」的那个，再找不到就用 panel）。" +
             "只换 RGB，**Alpha 保持 prefab 里调好的值**")]
    public SpriteRenderer tint;

    [Header("裁切（超出面板的立绘直接切断）")]
    [Tooltip("裁切矩形尺寸（世界单位；面板缩放保持 1）。留 0 = 自动取 panel 的尺寸。运行时可实时改")]
    public Vector2 clipSize = Vector2.zero;

    [Header("立绘在面板里的相对位置（运行时可实时调）")]
    [Tooltip("立绘偏移（**面板比例**）：x = 1 表示往右移动一整块面板宽，0.25 = 四分之一块，正数往右/上。" +
             "图是在网格内部滑动，网格和物体都不动")]
    public Vector2 portraitOffset = Vector2.zero;
    [Tooltip("立绘缩放：1 = 原图世界尺寸；>1 放大（面板里会露出更小的一段、更靠脸）")]
    public float portraitScale = 1f;
    [Tooltip("立绘的 sortingOrder（面板边框 -11 / 文本 -10）：想压在边框上就填 -10，想藏在边框下就填 -12")]
    public int portraitSortingOrder = -11;

    [Header("调试")]
    [Tooltip("每次应用布局打一行（stage/立绘名/裁切矩形/排序/偏移），排查「立绘没反应」用；嫌刷屏就关掉")]
    public bool logOnApply = true;

    private static readonly List<CharacterStatusArea> All = new();
    private static readonly int ClipRectId = Shader.PropertyToID("_ClipRect");
    private static readonly int ContentShiftId = Shader.PropertyToID("_ContentShift");

    private string lastContent = "";
    private SpriteEmotion lastFallback = SpriteEmotion.origin;
    private bool lastForce;

    private Sprite currentSprite;
    private Vector2 naturalSize;          // 立绘原图的世界尺寸
    private Vector4 lastRect;
    private Vector4 lastShift;
    private bool warnedShiftClobbered;
    private bool warnedShaderMissing;
    private MaterialPropertyBlock mpb;
    private bool warnedNoSprite;
    private bool warnedNoRenderer;
    private bool warnedNoMaterial;
    private bool warnedSharedMaterial;
    private bool warnedFrameContainer;

    // 已经应用上去的布局（用来判断运行时改了参数需不需要重算）
    private Vector2 appliedOffset = new(float.NaN, float.NaN);
    private Vector2 appliedClip = new(float.NaN, float.NaN);
    private float appliedScale = float.NaN;
    private int appliedSorting = int.MinValue;
    private int appliedStage = int.MinValue;

    private void OnEnable()
    {
        if (!All.Contains(this)) All.Add(this);
    }

    private void OnDisable()
    {
        All.Remove(this);
    }

    private void Start()
    {
        ApplyStageColor();
        // 开局先把中立立绘摆上（还没说话也能看到人），顺便让运行时的参数调整立刻有东西可看
        Apply(lastContent, "", lastFallback, lastForce);
    }

    private void Update()
    {
        // 阵营号在运行时被改也要跟着换色（只换 RGB，Alpha 不动）
        if (appliedStage != stage) ApplyStageColor();

        if (currentSprite == null) return;
        // 参数被改了就重新应用；没改一帧什么都不做（只是几次比较）
        if (appliedOffset == portraitOffset && appliedClip == clipSize
            && Mathf.Approximately(appliedScale, portraitScale) && appliedSorting == portraitSortingOrder) return;
        ApplyLayout();
    }

    /// <summary>
    /// AIAgent 说一句就推过来。stage = 阵营序号；fallbackEmo/forceEmo 和 UIMessageManager 一个口径
    /// （赢家定格时强制用 win，无视模型自己写的 [emo:xxx]）。
    /// </summary>
    public static void Push(int stage, string content,string _thinking, SpriteEmotion fallbackEmo = SpriteEmotion.origin, bool forceEmo = false)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i] == null || All[i].stage != stage) continue;
            All[i].Apply(content, _thinking, fallbackEmo, forceEmo);
            return;
        }
    }

    /// <summary>把一句公开发言贴到状态区：文本 + 立绘（表情从 [emo:xxx] 解析）。</summary>
    public void Apply(string content,string _thinking, SpriteEmotion fallbackEmo = SpriteEmotion.origin, bool forceEmo = false)
    {
        anim?.SetTrigger("newMessage");

        lastContent = content ?? "";
        lastFallback = fallbackEmo;
        lastForce = forceEmo;

        // 文本：和 UIMessageBar 一样，[emo:xxx] 抠掉、连续空行折成一个
        string shown = AIAgent.ExtractEmotion(lastContent, out bool hasEmo, out SpriteEmotion emo);
        if (speech != null) speech.PlayWithText(UIMessageBar.CollapseBlankLines(shown));
        if (thinking != null) thinking.PlayWithText(UIMessageBar.CollapseBlankLines(_thinking));

        if (portrait == null)
        {
            if (!warnedNoRenderer)
            {
                warnedNoRenderer = true;
                Debug.LogWarning($"[角色状态区] {name}：portrait 没接（把带 SceneSpriteDisplay 的「立绘」拖进这个字段）");
            }
            return;
        }

        SpriteEmotion want = forceEmo ? fallbackEmo : (hasEmo ? emo : fallbackEmo);
        Sprite sp = LoadPortrait(want);
        if (sp == null)
        {
            if (!warnedNoSprite)
            {
                warnedNoSprite = true;
                Debug.LogWarning($"[角色状态区] {name}：Resources 里找不到立绘 {AIAgent.GetStageName(stage)}_{want} 或 _origin");
            }
            return;
        }

        if (sp != currentSprite)
        {
            currentSprite = sp;
            naturalSize = SceneSpriteDisplay.GetSpriteWorldSize(sp);
            appliedScale = float.NaN;                                  // 换了图，网格必须重建
            appliedClip = new Vector2(float.NaN, float.NaN);
        }

        ApplyLayout();
    }

    /// <summary>把"立绘怎么摆"这套参数应用上去：缩放/裁切尺寸变了才重建网格，其余只是挪位置 + 写参数。</summary>
    public void ApplyLayout()
    {
        if (portrait == null || currentSprite == null) return;

        float scale = Mathf.Max(0.01f, portraitScale);
        Vector2 size = PanelSize();
        bool needRebuild = !Mathf.Approximately(appliedScale, scale) || appliedClip != clipSize;

        if (needRebuild)
        {
            portrait.sprite = currentSprite;
            portrait.padding = 0f;
            portrait.keepAspect = true;
            // 网格留出余量：expand 让网格比内容区大一圈，这样"图在网格里滑动"时不会被网格边缘切掉
            portrait.expand = Mathf.Max(size.x, size.y) * 1.5f;
            // 注意：SceneSpriteDisplay 上那个 public 的 frameSize 字段**没人读**（全文只有声明），
            // 真正生效的是 SetFrame() 写进去的 frameFallback —— 所以尺寸必须走 SetFrame，不能直接赋值 frameSize。
            // 它只在自身层级里已经有 ContainerMesh 时才会去改那个框，我们这里没有框，所以不会碰别人。
            if (portrait.FrameContainer != null)
            {
                if (!warnedFrameContainer)
                {
                    warnedFrameContainer = true;
                    Debug.LogWarning($"[角色状态区] {name}：立绘层级里有个 ContainerMesh，SetFrame 会改它的尺寸，已跳过缩放（把那个框挪走或删掉即可）");
                }
                portrait.Rebuild();
            }
            else
            {
                // 尺寸 = 原图世界尺寸 × portraitScale（内部会 Rebuild，重建会把网格子物体位移归零，下面重新挪）
                portrait.SetFrame(naturalSize * scale, 0f, 0f, Color.clear, Color.clear);
            }
        }

        MeshRenderer mr = MeshOf(portrait);
        if (mr == null)
        {
            if (!warnedNoRenderer)
            {
                warnedNoRenderer = true;
                Debug.LogWarning($"[角色状态区] {name}：立绘下面还没有网格（MeshRenderer）—— SceneSpriteDisplay 的 sprite 还没设上？");
            }
            return;
        }

        if (appliedSorting != portraitSortingOrder)
        {
            portrait.sortingOrder = portraitSortingOrder;
            mr.sortingOrder = portraitSortingOrder;     // 直接写渲染器，省一次重建
        }

        // 立绘物体 / 网格**一律不动**：偏移改成"图在网格里滑动"（_ContentShift），
        // 这样网格、裁切窗口、其它子物体都不会被拖走 —— 用户要的就是这个。
        // portraitOffset 是**面板比例**（1 = 一整块面板宽/高），先换成世界单位，再按网格缩放换成本地单位。
        Vector3 lossy = mr.transform.lossyScale;
        Vector2 worldShift = new Vector2(portraitOffset.x * size.x, portraitOffset.y * size.y);
        Vector2 shift = new Vector2(
            worldShift.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)),
            worldShift.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)));

        if (size.x > 0.0001f && size.y > 0.0001f)
        {
            if (mr.sharedMaterial == null)
            {
                if (!warnedNoMaterial)
                {
                    warnedNoMaterial = true;
                    Debug.LogWarning($"[角色状态区] {name}：立绘的材质是空的，没法写裁切矩形");
                }
            }
            else if (!portrait.instanceMaterial)
            {
                if (!warnedSharedMaterial)
                {
                    warnedSharedMaterial = true;
                    Debug.LogWarning($"[角色状态区] {name}：立绘的 instanceMaterial 关着，_ClipRect 会写到共享材质上、串到舞台去，已跳过裁切");
                }
            }
            else
            {
                // ⓪ 先确认材质的 shader 认不认识这两个参数 —— 不认识就是 shader 没重新编译（最常见的原因）
                Material mat = mr.sharedMaterial;
                if (!mat.HasProperty(ContentShiftId) || !mat.HasProperty(ClipRectId))
                {
                    if (!warnedShaderMissing)
                    {
                        warnedShaderMissing = true;
                        Debug.LogError($"[角色状态区] {name}：材质用的 shader（{mat.shader.name}）里没有 {ContentShiftId}/{ClipRectId} 这两个参数 —— " +
                                       "SpriteMeshDot.shader 没重新编译，或者材质不是这条 shader。点一下 Unity 窗口让它重编译");
                    }
                }

                // ① 图在网格里滑动（网格/物体都不动）
                Vector4 shiftValue = new Vector4(shift.x, shift.y, 0f, 0f);

                // ② 裁切窗口钉在面板上：面板世界矩形的两个对角各自换算到网格子物体本地空间
                // （不能按"世界单位 = 本地单位"直接算 —— 立绘或父物体只要有缩放就会偏）
                Vector3 panelCenter = transform.position;
                Vector3 half = new Vector3(size.x * 0.5f, size.y * 0.5f, 0f);
                Vector3 a = mr.transform.InverseTransformPoint(panelCenter - half);
                Vector3 b = mr.transform.InverseTransformPoint(panelCenter + half);

                lastRect = new Vector4(
                    Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                    Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

                // ③ 两条路都写：材质（持久，MPB 没带的参数就以它为准）+ 渲染器块（一定生效，优先级更高）
                mat.SetVector(ContentShiftId, shiftValue);
                mat.SetVector(ClipRectId, lastRect);

                mr.GetPropertyBlock(mpb ??= new MaterialPropertyBlock());
                mpb.SetVector(ContentShiftId, shiftValue);
                mpb.SetVector(ClipRectId, lastRect);
                mr.SetPropertyBlock(mpb);

                // ④ 读回来对一下：值没留在材质上就说明被别的东西盖掉了
                lastShift = mat.GetVector(ContentShiftId);
                if (!Mathf.Approximately(lastShift.x, shift.x) || !Mathf.Approximately(lastShift.y, shift.y))
                {
                    if (!warnedShiftClobbered)
                    {
                        warnedShiftClobbered = true;
                        Debug.LogWarning($"[角色状态区] {name}：写进去的平移没留在材质上（读到 {lastShift}，写的是 {shift}）—— 可能材质不是实例，或被别的系统改了");
                    }
                }
            }
        }

        appliedOffset = portraitOffset;
        appliedClip = clipSize;
        appliedScale = scale;
        appliedSorting = portraitSortingOrder;

        if (logOnApply)
            Debug.Log($"[角色状态区] {name} stage={stage} 立绘={currentSprite.name} 画在={mr.name}({mr.transform.parent.name}) 面板={PanelSize()} 网格={portrait.GetMeshSize()} " +
                      $"裁切={lastRect} 平移(比例)={portraitOffset} 本地平移={lastShift} 缩放={scale} 排序={portraitSortingOrder}");
    }

    /// <summary>
    /// 把状态区那根"底"换成这个阵营的颜色（口径和消息条一样：MapConfig.GetColor(stage)），
    /// **只换 RGB，Alpha 用 prefab 里调好的**。
    /// </summary>
    public void ApplyStageColor()
    {
        appliedStage = stage;

        SpriteRenderer target = tint != null ? tint : FindTint();
        if (target == null) return;

        MapConfig cfg = MapConfig.Instance;
        Color c = cfg != null ? cfg.GetColor(stage) : Color.white;
        c.a = target.color.a;                 // 保持 Alpha 不变
        target.color = c;

        c = cfg != null ? cfg.GetColor(stage,MapConfig.ColorStage.Bright) : Color.white;
        var textMesh = thinking.GetComponent<TextMeshPro>();
        textMesh.color = c;
    }

    /// <summary>没手动指定 tint 时：先找子孙里叫「底」的那个渲染器（prefab 里就是它），再退回 panel。</summary>
    private SpriteRenderer FindTint()
    {
        SpriteRenderer[] all = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == "底") return tint = all[i];

        if (panel == null) panel = GetComponent<SpriteRenderer>();
        return tint = panel;
    }

    private Sprite LoadPortrait(SpriteEmotion emo)
    {
        string name = AIAgent.GetStageName(stage);
        Sprite sp = Resources.Load<Sprite>(name + "_" + emo);
        if (sp == null) sp = Resources.Load<Sprite>(name + "_" + SpriteEmotion.origin);
        return sp;
    }

    /// <summary>面板尺寸：clipSize 填了就用它，否则按 panel 的 SpriteRenderer 尺寸 × 缩放算。</summary>
    private Vector2 PanelSize()
    {
        if (clipSize.x > 0.0001f && clipSize.y > 0.0001f) return clipSize;

        if (panel == null) panel = GetComponent<SpriteRenderer>();
        if (panel == null || panel.sprite == null) return Vector2.zero;

        Vector2 size = panel.drawMode == SpriteDrawMode.Simple
            ? (Vector2)panel.sprite.bounds.size
            : panel.size;
        Vector3 scale = panel.transform.lossyScale;
        return new Vector2(size.x * Mathf.Abs(scale.x), size.y * Mathf.Abs(scale.y));
    }

    /// <summary>
    /// 找"真正在画这张图"的渲染器。**必须跳过组件自己身上的那个**：
    /// SceneSpriteDisplay 是把图做在它自己创建的 SM_ 子物体上（自建网格 + 实例材质），
    /// 组件所在物体上的 MeshRenderer 往往是空的（prefab 里随手加的），参数写到它身上等于白写。
    /// </summary>
    private static MeshRenderer MeshOf(SceneSpriteDisplay display)
    {
        if (display == null) return null;

        MeshRenderer[] all = display.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < all.Length; i++)
        {
            MeshRenderer r = all[i];
            if (r == null || r.transform == display.transform) continue;   // 跳过组件自己身上那个
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;             // 只认真的有网格的
            return r;
        }

        // 兜底：没有带网格的子物体时，按老办法取（至少不会空引用）
        return display.GetComponentInChildren<MeshRenderer>(true);
    }
}
