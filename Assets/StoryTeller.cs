using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;

/// <summary>
/// 暂停舞台（叙事场景）总控。
///
/// 舞台是场景里一块独立的取景区域：挂在「叙事」物体上（世界坐标 x=32，远离战场），
/// 由子物体 scene_camera 单独取景；OpenScene 打开它，并用 Background 的毛玻璃盖住战场。
/// 演出内容全部作为本物体的子物体生成，本地坐标即舞台坐标
/// （正交 size=5 → y∈[-5,5]，x∈±5*aspect，16:9 时约 ±8.9）。
///
/// 时钟：不要用 Time.deltaTime / Time.time。舞台动画逐「录制帧」推进，
/// 这样录出来的视频里运动是均匀的、不受实时帧率抖动影响。所有协程都用 WaitForCaptureUpdate()
/// 等帧、用 CaptureDeltaTime 累加时长。录制器没在产帧时（未录制或已暂停）由 RealTimeUpdate
/// 用 Task.Delay 补齐，保证编辑器里预览也能动。
///
/// 用法：StoryTeller.Instance.Play(new XxxScene(...)) —— 舞台会自动开关，多个场景按加入顺序排演。
/// </summary>
public class StoryTeller : MonoBehaviour
{
    [Header("相机设置")]
    public Camera scene_camera;
    [Header("背景参数设置")]
    public SpriteRenderer BackgroundSprite;
    public float BackgroundDuration = 0.5f;
    public float BackgroundBlur = 12f;
    public float BackgroundTransparent = 0.75f;
    public AnimationCurve BackgroundCurve = AnimationCurve.Linear(0, 0, 1, 1);
    private bool IsBackground = false;
    public bool test;
    public bool testMove;
    [Header("场景")]
    public static StoryTeller Instance;

    [Header("生成模板（Prefab/Scene 下的两件套）")]
    [Tooltip("文本卡模板：Assets/Prefab/Scene/Text.prefab（圆角容器 + TMP 台词）")]
    public GameObject GoText;
    [Tooltip("图片卡模板：Assets/Prefab/Scene/Mesh.prefab（圆角容器 + Mesh 显示 sprite）")]
    public GameObject GoPicture;

    [Header("演出开关")]
    [Tooltip("关掉后三个演出场景全部不播，退回原来的飘字 / 横幅表现")]
    public bool enableScenes = true;
    [Tooltip("演出期间是否压住原有飘字 / 立绘列表，避免同一句话重复显示")]
    public bool suppressLegacySpeech = true;

    [Header("升级选项图标（留空则只显示文字）")]
    public Sprite iconMarble;
    public Sprite iconTurret;
    public Sprite iconShield;

    [Header("图片卡材质")]
    [Tooltip("图片卡（立绘 / 图标）的材质覆盖。留空 = 不动 Mesh.prefab 自带的那份（当前是 SpriteMeshDot 网点）；" +
             "想换成正常 sprite 显示就把 Resources/Material/SpriteMeshDefault 拖进来")]
    public Material pictureMaterial;

    private Vector3 LeftBottonPos;
    private Vector3 RightTopPos;
    private float Width;
    private float Height;
    [Header("TestCircle")]
    public Transform circle;
    [Header("录制和内置时钟")]
    public float CaptureFrame = 60f;
    private float _CaptureDeltaTime = float.NaN;

    private float CaptureDeltaTime {
        get
        {
            if (float.IsNaN(_CaptureDeltaTime))
            {
                _CaptureDeltaTime = 1f / CaptureFrame;
            }
            return _CaptureDeltaTime;
        }
    }
    private int frame = 0;
    private static bool stop = false;

    #region 初始化

    private void Awake()
    {
        Instance = this;
        stop = false;
        Time.timeScale = 1;
        FitStageToCamera();   // 背景按相机视口铺满
        CapturePause.Capture.CaptureUpdate += CaptureUpdate;//在CapturePause.Capture里注册时钟
        _ = RealTimeUpdate();
    }

    /// <summary>
    /// 舞台开着 = 战场必须冻结。OpenScene 已经把 timeScale 压成 0，但外部还会把它解开：
    /// 非实时录制下 AVPro 的 PauseCapture / ResumeCapture 会直接把 timeScale 写成 0 / 1
    /// （插件里那句 TODO 自己都承认「不该假设恢复时是 1」）。升级选择 / 悄悄话这类请求都可能在
    /// 舞台期间暂停一次录制，恢复录制时就会把冻结漏掉，演出没播完战场就活了。
    /// 这里在舞台还开着的时候把 timeScale 压回 0；CloseScene 一进来 IsBackground 就变 false
    /// （关场淡出过程中不再强制），所以不会和 ECloseScene 恢复时间打架。
    /// </summary>
    private void Update()
    {
        if (IsBackground && Time.timeScale != 0f) Time.timeScale = 0f;
    }
    #endregion

    #region 时钟
    public void CaptureUpdate()
    {
        frame++;
    }
    public IEnumerator WaitForCaptureUpdate()
    {
        int lastframe = frame;
        yield return new WaitUntil(() => frame > lastframe);
    }

    /// <summary>舞台时钟已经推进过的帧数（每 tick +1）。只读，给要跟舞台同一个钟的动画用（比如 Text 的逐字入场）。</summary>
    public int ClockFrame => frame;
    /// <summary>一个舞台帧等于多少秒（= 1 / CaptureFrame）。</summary>
    public float ClockDelta => 1f / Mathf.Max(1f, CaptureFrame);

    private async Task RealTimeUpdate()
    {
        while (!stop)
        {
            await Task.Delay((int)(CaptureDeltaTime * 1000));
            if (!CapturePause.Capture.IsCapturing()) CaptureUpdate();
        }
    }
    private void OnDestroy()
    {
        stop = true;
        print("SceneClockStop");
    }
    private void OnApplicationQuit()
    {
        stop = true;
        print("SceneClockStop");
    }

    private int _lastViewW, _lastViewH;

    #endregion

    #region 舞台坐标

    /// <summary>舞台可视范围（世界单位，以舞台原点为中心）。</summary>
    public Vector2 StageSize { get { EnsureStageSize(); return new Vector2(Width, Height); } }
    /// <summary>半宽（x 方向）：正交相机按屏幕宽高比算出来的，通常是半高的 1.78 倍。</summary>
    public float HalfWidth { get { EnsureStageSize(); return Width * 0.5f; } }
    /// <summary>半高（y 方向）= 正交 size。</summary>
    public float HalfHeight { get { EnsureStageSize(); return Height * 0.5f; } }

    /// <summary>编辑期也能拿到舞台尺寸：Awake 之外首次访问时补算一次。</summary>
    private void EnsureStageSize()
    {
        if (Width > 0f && Height > 0f) return;
        if (scene_camera == null) return;
        LeftBottonPos = scene_camera.ScreenToWorldPoint(new Vector3(0f, 0f, 0f));
        RightTopPos = scene_camera.ScreenToWorldPoint(new Vector3(scene_camera.pixelWidth, scene_camera.pixelHeight, 0f));
        Vector3 v3 = RightTopPos - LeftBottonPos;
        Width = v3.x;
        Height = v3.y;
    }

    /// <summary>
    /// 按相机重算舞台尺寸，并让背景铺满相机可视范围。
    /// 相机视口 / 分辨率是会变的（编辑器窗口缩放、录制换分辨率），只靠 Awake 算一次就会一直用旧值，
    /// 所以每次开舞台都重算：换算仍然走 ScreenToWorldPoint 那两个角（LeftBottonPos / RightTopPos）。
    /// </summary>
    private void FitStageToCamera()
    {
        Width = 0f;
        Height = 0f;                 // 清掉缓存，强制重新换算
        EnsureStageSize();
        FitBackground();
    }

    /// <summary>把 BackgroundSprite 摆到相机视口中心、缩放到正好铺满 Width × Height（自适应分辨率）。</summary>
    private void FitBackground()
    {
        if (BackgroundSprite == null) return;
        if (Width <= 0f || Height <= 0f) return;

        Transform t = BackgroundSprite.transform;
        Vector3 center = (LeftBottonPos + RightTopPos) * 0.5f;
        t.position = new Vector3(center.x, center.y, t.position.z);

        Sprite sprite = BackgroundSprite.sprite;
        Vector2 spriteSize = sprite != null ? (Vector2)sprite.bounds.size : Vector2.one;
        if (spriteSize.x <= 1e-4f || spriteSize.y <= 1e-4f) spriteSize = Vector2.one;

        // 父物体可能有缩放，本地缩放要除掉它，铺出来才是 Width × Height
        Vector3 parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
        t.localScale = new Vector3(
            Width / (spriteSize.x * Mathf.Max(1e-4f, Mathf.Abs(parentScale.x))),
            Height / (spriteSize.y * Mathf.Max(1e-4f, Mathf.Abs(parentScale.y))),
            1f);
    }

    /// <summary>全局快捷入口：没挂 StoryTeller 时返回 null。</summary>
    public static StoryTeller Stage => Instance;

    /// <summary>演出是否可用（物体存在 + 开关打开）。</summary>
    public static bool CanPlay => Instance != null && Instance.enableScenes;

    /// <summary>要不要压住原有飘字 / 立绘列表（演出开关关闭时不该压）。</summary>
    public static bool SuppressLegacy => CanPlay && Instance.suppressLegacySpeech;

    #endregion

    #region 场景支持

    // 舞台上的内容按「块」处理：一块文字 / 一块图片在开场时从某个方向平移进来，
    // 样式与位置都写在 Item 上，由 StoryTeller 统一实例化与写值（像前端把数据、布局、渲染分开）。
    public enum Direction
    {
        Left,
        Right,
        Top,
        Botton,
    }

    public enum ItemType
    {
        Text,
        Picture,
    }

    /// <summary>
    /// 舞台上的一块内容。Text 用 Text.prefab（圆角容器 + TMP），Picture 用 Mesh.prefab（圆角容器 + Mesh 显示 sprite）。
    /// 只描述「长什么样、在哪」，实例化与写值由 StoryTeller 做。
    /// </summary>
    public class Item
    {
        public ItemType type;
        public GameObject realOb;

        // —— 内容 ——
        /// <summary>Text 用文案；Picture 用 Resources 图片路径（不含扩展名）。</summary>
        public string str1 = "";
        /// <summary>Picture 的直接引用（优先于 str1 的 Resources 路径）。</summary>
        public Sprite sprite;

        // —— 布局（舞台本地坐标 / 世界单位）——
        public Vector2 position = Vector2.zero;
        public Vector2 size = new Vector2(4f, 2f);
        public float angle = 0f;

        // —— 外观 ——
        /// <summary>容器底色。</summary>
        public Color containerColor = new Color(0.21960784f, 0.21960784f, 0.21960784f, 1f);
        /// <summary>容器墙环（描边）颜色。</summary>
        public Color wallColor = Color.white;
        /// <summary>文字颜色 / 图片染色。</summary>
        public Color textColor = Color.white;
        public float cornerRadius = 0.5f;
        public float wallWidth = 0.08f;
        /// <summary>关掉时容器整体透明，只留文字 / 图片本体。</summary>
        public bool containerVisible = true;
        /// <summary>渲染顺序：同位置叠放时（选项框底板 + 文案层）靠它定先后，文字会自动再 +2。</summary>
        public int sortingOrder = 1;

        /// <summary>整体透明度（0~1）。SetAlpha 会记在这里：ApplyItem 重写样式后要靠它把透明度接回去。</summary>
        public float alpha = 1f;

        // —— 入场 ——
        public Direction enterFrom = Direction.Botton;
        public float enterDistance = 6f;
        public float enterDuration = 0.45f;

        /// <summary>整体缩放（1 = prefab 原样）。「选中弹一下」就是改它。</summary>
        public Vector2 scale = Vector2.one;
        /// <summary>图片卡的显示区放大倍率（立绘四周的透明留白靠它顶掉）。</summary>
        public float spriteZoom = 1f;

        /// <summary>图片卡四边渐变：(开始比例, 结束比例, 开始透明度, 结束透明度)。默认 (0,0,1,1) = 不渐变。</summary>
        public Vector4 edgeTop = new Vector4(0f, 0f, 1f, 1f);
        public Vector4 edgeRight = new Vector4(0f, 0f, 1f, 1f);
        public Vector4 edgeBottom = new Vector4(0f, 0f, 1f, 1f);
        public Vector4 edgeLeft = new Vector4(0f, 0f, 1f, 1f);

        /// <summary>四边渐变的贝塞尔中间点：(在渐变区间里的比例 0~1, 该点的透明度)。y &lt; 0 = 线性、不插控制点。</summary>
        public Vector2 edgeTopMid = new Vector2(0.5f, -1f);
        public Vector2 edgeRightMid = new Vector2(0.5f, -1f);
        public Vector2 edgeBottomMid = new Vector2(0.5f, -1f);
        public Vector2 edgeLeftMid = new Vector2(0.5f, -1f);

        /// <summary>图片卡的整体可见度（网点口径，对应 shader 的 _Alpha）。1 = 原图。</summary>
        public float spriteVisibility = 1f;
        /// <summary>正在飘动的协程（Float 用）。滑入 / 滑出会先停掉它，免得两个动画抢同一个坐标。</summary>
        [System.NonSerialized] public Coroutine bob;

        /// <summary>创建后的「落定补刷」是否已排过（每块卡只补一次：等一帧后按最终字段再刷一遍显示）。</summary>
        [System.NonSerialized] public bool displaySettleDone;

        // —— 文字专用 ——
        /// <summary>字号（0 = 用 prefab 自带值）。注意场景里统一走 StageStyle.FontSize(世界单位高)。</summary>
        public float fontSize = 0f;
        /// <summary>文案写完后是否播逐字入场。</summary>
        public bool playTextIn = false;
        /// <summary>文字对齐：正文卡 TopLeft，标题/标签 Center。</summary>
        public TextAlignmentOptions align = TextAlignmentOptions.Center;
        /// <summary>文字四周内边距（世界单位）。</summary>
        public float textPadding = 0f;
        /// <summary>文字描边宽度（0 = 不描边）。</summary>
        public float textOutlineWidth = 0f;
        /// <summary>描边颜色。</summary>
        public Color textOutlineColor = Color.black;
        /// <summary>字面膨胀：描边时加一点字更实（0 = 不动）。</summary>
        public float textFaceDilate = 0f;
        /// <summary>文字放不下时怎么办：默认 Overflow（溢出到框外）。正文卡用 Ellipsis，免得压到别的卡上。</summary>
        public TextOverflowModes textOverflow = TextOverflowModes.Overflow;

        // —— 运行时缓存的部件 ——
        public SceneTextDisplay textDisplay;
        public SceneSpriteDisplay spriteDisplay;

        public bool IsAlive => realOb != null;

        public void Delete()
        {
            StopBob();
            if (realOb != null) Destroy(realOb);
            realOb = null;
            textDisplay = null;
            spriteDisplay = null;
        }

        /// <summary>移到舞台本地坐标 p（并把记录的位置同步过去）。</summary>
        public void SetLocalPosition(Vector2 p)
        {
            position = p;
            if (realOb == null) return;
            Vector3 lp = realOb.transform.localPosition;
            realOb.transform.localPosition = new Vector3(p.x, p.y, lp.z);
        }

        /// <summary>写整体缩放（记在 Item 上，滑入 / 滑出和重建都不会抹掉）。</summary>
        public void SetScale(Vector2 s)
        {
            scale = s;
            if (realOb == null) return;
            realOb.transform.localScale = new Vector3(s.x, s.y, 1f);
        }

        /// <summary>停掉正在跑的 Float（滑入 / 滑出会自动调它）。</summary>
        public void StopBob()
        {
            if (bob == null) return;
            if (Instance != null) Instance.StopCoroutine(bob);
            bob = null;
        }

        public Vector3 LocalPosition => realOb != null ? realOb.transform.localPosition : Vector3.zero;

        /// <summary>整体透明度（0~1）：同时作用在容器、文字、图片本体上。</summary>
        public void SetAlpha(float a)
        {
            a = Mathf.Clamp01(a);
            if (Mathf.Approximately(alpha, a)) return;   // 早退：相同透明度不再向下刷，避免重复触发 ContainerMesh/Sprite
            alpha = a;
            textDisplay?.SetAlpha(a);
            spriteDisplay?.SetAlpha(a);
        }

        /// <summary>按 Item 上的 enterFrom / enterDistance / enterDuration 生成入场描述。</summary>
        public IEnumerator Enter()
        {
            if (Instance == null) yield break;
            yield return Instance.SlideIn(this, enterFrom, enterDistance, enterDuration);
        }
    }

    #endregion

    #region 生成 / 写值

    /// <summary>生成一块文本卡（Text.prefab）。模板没配或生成失败时返回 null。</summary>
    public Item CreateText(string content, Vector2 position, Vector2 size)
    {
        Item item = InstantiateItem(ItemType.Text, position);
        if (item == null) return null;
        item.str1 = content;
        item.size = size;
        ApplyItem(item);
        return item;
    }

    /// <summary>生成一块图片卡（Mesh.prefab）。</summary>
    public Item CreatePicture(Sprite sprite, Vector2 position, Vector2 size)
    {
        Item item = InstantiateItem(ItemType.Picture, position);
        if (item == null) return null;
        item.sprite = sprite;
        item.size = size;
        ApplyItem(item);
        return item;
    }

    /// <summary>生成一块图片卡（Mesh.prefab），sprite 走 Resources 路径（不含扩展名）。</summary>
    public Item CreatePicture(string spriteResourcePath, Vector2 position, Vector2 size)
    {
        Item item = InstantiateItem(ItemType.Picture, position);
        if (item == null) return null;
        item.str1 = spriteResourcePath;
        item.size = size;
        ApplyItem(item);
        return item;
    }

    /// <summary>立绘所在的 Resources 子目录（立绘资源都在 Assets/Resources/Sprite/ 下，文件名仍是「名字_表情」）。</summary>
    public const string PortraitFolder = "Sprite/";

    /// <summary>按阵营取一张立绘（Resources/Sprite/ 下的「名字_表情」），拿不到就退回 origin。</summary>
    public static Sprite LoadPortrait(int stage, SpriteEmotion emo)
    {
        string name = AIAgent.GetStageName(stage);
        if (string.IsNullOrEmpty(name)) return null;
        Sprite sp = Resources.Load<Sprite>(PortraitFolder + name + "_" + emo.ToString());
        if (sp == null) sp = Resources.Load<Sprite>(PortraitFolder + name + "_" + SpriteEmotion.origin.ToString());
        return sp;
    }

    /// <summary>升级选项对应的图标（没配就返回 null）。</summary>
    public Sprite UpgradeIcon(int choice)
    {
        switch (choice)
        {
            case 2: return iconTurret;
            case 3: return iconShield;
            default: return iconMarble;
        }
    }

    private Item InstantiateItem(ItemType type, Vector2 position)
    {
        GameObject prefab = type == ItemType.Text ? GoText : GoPicture;
        if (prefab == null)
        {
            Debug.LogError($"[StoryTeller] 生成 {type} 失败：模板没配（GoText / GoPicture 为空）。");
            return null;
        }

        Item item = new Item { type = type, position = position };
        item.realOb = Instantiate(prefab, transform);
        item.realOb.name = type == ItemType.Text ? "StageText" : "StagePicture";
        item.realOb.transform.localPosition = new Vector3(position.x, position.y, 0f);
        item.realOb.transform.localRotation = Quaternion.identity;
        item.realOb.transform.localScale = Vector3.one;

        item.textDisplay = item.realOb.GetComponent<SceneTextDisplay>();
        if (item.textDisplay == null) item.textDisplay = item.realOb.GetComponentInChildren<SceneTextDisplay>(true);
        item.spriteDisplay = item.realOb.GetComponent<SceneSpriteDisplay>();
        if (item.spriteDisplay == null) item.spriteDisplay = item.realOb.GetComponentInChildren<SceneSpriteDisplay>(true);

        // 新建的卡片先藏起来：它的入场动画可能好几帧（甚至几秒）之后才排到，
        // 不藏的话观众会先看到它「已经站在最终位置」，然后才跳回起点重新滑进来。
        // 之后 SlideIn 会从 alphaFrom 淡到 1，正好把它接上来。
        item.SetAlpha(0f);

        spawnedItems.Add(item);
        return item;
    }

    /// <summary>把 Item 上描述的样式与内容写进 prefab 实例的部件里。</summary>
    public void ApplyItem(Item item)
    {
        if (item == null || item.realOb == null) return;

        item.realOb.transform.localPosition = new Vector3(item.position.x, item.position.y, item.realOb.transform.localPosition.z);
        item.realOb.transform.localEulerAngles = new Vector3(0f, 0f, item.angle);
        item.realOb.transform.localScale = new Vector3(item.scale.x, item.scale.y, 1f);

        Color wall = ContainerVisible(item, item.wallColor);
        Color bg = ContainerVisible(item, item.containerColor);

        if (item.textDisplay != null)
        {
            item.textDisplay.content = item.str1;
            item.textDisplay.size = item.size;
            item.textDisplay.cornerRadius = item.cornerRadius;
            item.textDisplay.wallWidth = item.wallWidth;
            item.textDisplay.wallColor = wall;
            item.textDisplay.containerColor = bg;
            item.textDisplay.textColor = item.textColor;
            item.textDisplay.sortingOrder = item.sortingOrder;
            item.textDisplay.alignment = item.align;
            item.textDisplay.textPadding = item.textPadding;
            item.textDisplay.outlineWidth = item.textOutlineWidth;
            item.textDisplay.outlineColor = item.textOutlineColor;
            item.textDisplay.faceDilate = item.textFaceDilate;
            item.textDisplay.overflow = item.textOverflow;
            if (item.fontSize > 0f) item.textDisplay.fontSize = item.fontSize;
            item.textDisplay.Apply();

            if (item.playTextIn) item.textDisplay.PlayIn();
        }

        if (item.spriteDisplay != null)
        {
            ApplySpriteDisplay(item);
        }
        // 临时排查用：每块图片卡写完之后把**实际生效**的尺寸打出来（底板框 / 画面 / 是否可见）。
        // 舞台上的卡都是运行时生成的，Inspector 里看不到，只有这行能看出是哪一层没生效。
        if (item.spriteDisplay != null)
        {
            Vector2 fr = item.spriteDisplay.GetFrameSize();
            Vector2 dr = item.sprite != null ? item.spriteDisplay.GetDrawnSize(item.sprite) : Vector2.zero;
        }

        // 兜底（对应「手动点一次刷新/重建才能恢复」的问题）：
        // 创建当帧的重建可能吃到还没完全落定的状态（各组件 OnEnable 的先后、prefab 改写的覆盖顺序），
        // 残留的就是旧几何 —— 表现为 SceneSpriteDisplay / ContainerMesh 显示不全。
        // 等一帧后所有字段都是最终值，这时把显示部件再刷一遍：等价于替用户点了一次 rebuild，
        // 但只动显示（ApplyAppearance / SetFrame+Rebuild），不动坐标、缩放和透明度，入场动画该怎么播还怎么播。
        if (!item.displaySettleDone && isActiveAndEnabled)
        {
            item.displaySettleDone = true;
            StartCoroutine(DeferredDisplayRefresh(item));
        }
    }


    /// <summary>把 Item 上的图片卡外观完整写进 SceneSpriteDisplay，并重建容器 + 网格。ApplyItem 与一帧后的兜底刷新共用这一条路径。</summary>
    private void ApplySpriteDisplay(Item item)
    {
        if (item == null || item.spriteDisplay == null) return;
        SceneSpriteDisplay d = item.spriteDisplay;

        // 没给 sprite 也没给路径时清空，否则会留下面板自带的那张图
        if (item.sprite != null) d.sprite = item.sprite;
        else if (!string.IsNullOrEmpty(item.str1)) d.sprite = Resources.Load<Sprite>(item.str1);
        else d.sprite = null;

        // 颜色写完必须把整体透明度乘进去：ApplyItem 是「按 Item 重写一遍样式」，
        // 新建卡片 alpha=0 藏到自己的入场动画，不能在这里被点亮。
        Color spriteTint = item.textColor;
        spriteTint.a *= Mathf.Clamp01(item.alpha);
        d.color = spriteTint;
        d.sortingOrder = item.sortingOrder;
        d.zoom = item.spriteZoom;
        d.visibility = item.spriteVisibility;
        d.edgeTop = item.edgeTop;
        d.edgeRight = item.edgeRight;
        d.edgeBottom = item.edgeBottom;
        d.edgeLeft = item.edgeLeft;
        d.edgeTopMid = item.edgeTopMid;
        d.edgeRightMid = item.edgeRightMid;
        d.edgeBottomMid = item.edgeBottomMid;
        d.edgeLeftMid = item.edgeLeftMid;
        if (pictureMaterial != null) d.material = pictureMaterial;

        d.SetFrame(item.size, item.cornerRadius, item.wallWidth, ContainerVisible(item, item.wallColor), ContainerVisible(item, item.containerColor), item.sortingOrder);
        d.Rebuild();
    }
    /// <summary>
    /// 等一帧，把一块卡的显示部件按**当时的最终字段**再刷一遍：
    /// 文本卡只刷容器外观（ApplyAppearance，不动文案、不重播逐字动画），
    /// 图片卡按 Item 尺寸重刷 SetFrame + Rebuild（SceneSpriteDisplay 与 ContainerMesh 一起重建）。
    /// 这就是用户验证过「点一下刷新/重建就能好」的那一步，现在让舞台自动补上。
    /// 透明度由各组件自己的字段带过去（SceneTextDisplay.currentAlpha / SceneSpriteDisplay.color.a），淡入淡出不被打断。
    /// </summary>
    private IEnumerator DeferredDisplayRefresh(Item item)
    {
        yield return WaitForCaptureUpdate();
        if (item == null || item.realOb == null) yield break;

        if (item.textDisplay != null)
        {
            item.textDisplay.ApplyAppearance();
        }
        if (item.spriteDisplay != null)
        {
            ApplySpriteDisplay(item);
        }
    }

    /// <summary>容器不可见时把颜色保留、把 alpha 归零（尺寸仍然参与取景）。</summary>
    private static Color ContainerVisible(Item item, Color c)
    {
        if (item.containerVisible) return c;
        c.a = 0f;
        return c;
    }

    #endregion

    #region 动画

    private static Vector2 DirectionOffset(Direction dir, float distance)
    {
        switch (dir)
        {
            case Direction.Left: return new Vector2(-distance, 0f);
            case Direction.Right: return new Vector2(distance, 0f);
            case Direction.Top: return new Vector2(0f, distance);
            default: return new Vector2(0f, -distance);
        }
    }

    /// <summary>等 stage 秒（按录制帧推进，不受 Time.timeScale 影响）。</summary>
    public IEnumerator WaitStage(float seconds)
    {
        if (seconds <= 0f) yield break;
        float t = 0f;
        while (t < seconds)
        {
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
        }
    }

    /// <summary>
    /// 开场平移：从 from 方向偏离 distance 的位置滑到 item.position，同时从 alphaFrom 淡到 1。
    /// scaleFrom 给了的话再叠一个「小 → 原大」的缩放（立绘/卡片用 0.9 这类值，进场更有分量）。
    /// duration <= 0 时直接就位。
    /// </summary>
    public IEnumerator SlideIn(Item item, Direction from, float distance, float duration, float alphaFrom = 0f, Vector2? scaleFrom = null)
    {
        if (item == null || item.realOb == null) yield break;
        item.StopBob();

        Vector2 target = item.position;
        Vector2 start = target + DirectionOffset(from, distance);
        Vector2 scaleTo = item.scale;
        if (scaleFrom.HasValue) item.SetScale(scaleFrom.Value);

        if (duration <= 0f)
        {
            item.SetLocalPosition(target);
            item.SetAlpha(1f);
            if (scaleFrom.HasValue) item.SetScale(scaleTo);
            yield break;
        }

        item.SetLocalPosition(start);
        item.SetAlpha(alphaFrom);

        float t = 0f;
        while (t < duration)
        {
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            k = k * k * (3f - 2f * k);   // smoothstep：前端常见的缓出
            item.SetLocalPosition(Vector2.Lerp(start, target, k));
            item.SetAlpha(Mathf.Lerp(alphaFrom, 1f, k));
            if (scaleFrom.HasValue) item.SetScale(Vector2.Lerp(scaleFrom.Value, scaleTo, k));
        }

        item.SetLocalPosition(target);
        item.SetAlpha(1f);
        if (scaleFrom.HasValue) item.SetScale(scaleTo);
    }

    /// <summary>滑出：滑向 to 方向 distance 处并淡到 0。</summary>
    public IEnumerator SlideOut(Item item, Direction to, float distance, float duration)
    {
        if (item == null || item.realOb == null) yield break;
        item.StopBob();

        Vector2 start = item.position;
        Vector2 target = start + DirectionOffset(to, distance);

        if (duration <= 0f)
        {
            item.SetLocalPosition(target);
            item.SetAlpha(0f);
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            k = k * k * (3f - 2f * k);
            item.SetLocalPosition(Vector2.Lerp(start, target, k));
            item.SetAlpha(1f - k);
        }

        item.SetLocalPosition(target);
        item.SetAlpha(0f);
    }

    /// <summary>缩放弹一下（from 倍 → to 倍，smoothstep）。用来表示「选中了这一项」。</summary>
    public IEnumerator Pop(Item item, Vector2 from, Vector2 to, float duration)
    {
        if (item == null || item.realOb == null) yield break;

        if (duration <= 0f)
        {
            item.SetScale(to);
            yield break;
        }

        item.SetScale(from);
        float t = 0f;
        while (t < duration)
        {
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
            float k = Mathf.Clamp01(t / duration);
            k = k * k * (3f - 2f * k);
            item.SetScale(Vector2.Lerp(from, to, k));
        }
        item.SetScale(to);
    }

    /// <summary>
    /// 让一块内容在原地缓慢上下浮动：等人的时候不至于像张死图。
    /// 滑入 / 滑出会自动停掉它，Item 被销毁也会停。
    /// </summary>
    public void Float(Item item, float amplitude = 0.1f, float period = 2.6f)
    {
        if (item == null || item.realOb == null) return;
        item.StopBob();
        item.bob = StartCoroutine(FloatRoutine(item, amplitude, period));
    }

    private IEnumerator FloatRoutine(Item item, float amplitude, float period)
    {
        float baseY = item.position.y;
        float t = 0f;
        period = Mathf.Max(0.01f, period);

        while (item != null && item.realOb != null)
        {
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
            Vector3 lp = item.realOb.transform.localPosition;
            item.realOb.transform.localPosition = new Vector3(
                lp.x,
                baseY + Mathf.Sin(t / period * Mathf.PI * 2f) * amplitude,
                lp.z);
        }
        if (item != null) item.bob = null;
    }

    /// <summary>一次性把若干块内容同时滑入（每块用各自的入场参数）。</summary>
    public IEnumerator SlideInAll(IEnumerable<Item> batch)
    {
        var list = new List<Item>();
        if (batch != null) foreach (Item it in batch) if (it != null) list.Add(it);

        int remaining = list.Count;
        for (int i = 0; i < list.Count; i++)
        {
            Item item = list[i];
            StartCoroutine(CountdownRoutine(SlideIn(item, item.enterFrom, item.enterDistance, item.enterDuration), () => remaining--));
        }
        while (remaining > 0) yield return WaitForCaptureUpdate();
    }

    private IEnumerator CountdownRoutine(IEnumerator inner, System.Action onDone)
    {
        yield return inner;
        onDone?.Invoke();
    }

    /// <summary>高亮 / 恢复：在容器颜色上叠一层，用来表示「选中了这一项」。</summary>
    public void SetHighlight(Item item, bool on, Color accent)
    {
        if (item == null || item.realOb == null) return;

        Color wall = on ? Color.Lerp(item.wallColor, accent, 0.85f) : item.wallColor;
        Color bg = on ? Color.Lerp(item.containerColor, accent, 0.22f) : item.containerColor;
        Color wallOut = ContainerVisible(item, wall);
        Color bgOut = ContainerVisible(item, bg);

        if (item.textDisplay != null)
        {
            item.textDisplay.wallColor = wallOut;
            item.textDisplay.containerColor = bgOut;
            item.textDisplay.ApplyAppearance();
        }
        if (item.spriteDisplay != null)
        {
            if (item.sprite == null)
            {
                // 无图底板：只更新 ContainerMesh 颜色，不要走 SetFrame 传尺寸，避免 0,0 把背景尺寸写坏
                ContainerMesh c = item.spriteDisplay.FrameContainer;
                if (c != null)
                {
                    c.wall_color = wallOut;
                    c.background_color = bgOut;
                    c.sortingOrder = item.sortingOrder;
                    c.Rebuild();
                }
            }
            else
            {
                item.spriteDisplay.SetFrame(item.size, item.cornerRadius, item.wallWidth, wallOut, bgOut, item.sortingOrder);
            }
        }
    }

    /// <summary>压暗 / 恢复（没被选中的选项）。</summary>
    public void SetDimmed(Item item, bool on)
    {
        if (item == null || item.realOb == null) return;
        SetHighlight(item, false, Color.white);
        item.SetAlpha(on ? 0.35f : 1f);
    }

    #endregion

    #region 场景出入管理

    private readonly List<Item> spawnedItems = new();
    /// <summary>本次演出生成的所有内容（只读）。</summary>
    public IReadOnlyList<Item> Items => spawnedItems;

    /// <summary>舞台当前是否打开。</summary>
    public bool IsOpen => IsBackground;

    /// <summary>
    /// 打开舞台。已经在舞台上时会先清场（支持连续换场）。
    /// immediately = true 时背景不做过场，直接到位。
    /// </summary>
    public void OpenScene(bool immediately = false)
    {
        Time.timeScale = 0;
        if (IsBackground)
        {
            ClearScene(true);
            return;
        }
        IsBackground = true;
        if (scene_camera != null) scene_camera.enabled = true;
        FitStageToCamera();   // 背景按当前相机视口自适应（分辨率变了也铺满）
        ClearScene(true);
        StartCoroutine(EBackGroundSwitch(immediately ? 0f : BackgroundDuration, true));
    }

    /// <summary>关闭舞台：清场 + 背景淡出 + 关掉舞台相机。</summary>
    public void CloseScene(bool immediately = false)
    {
        if (!IsBackground) return;
        IsBackground = false;
        StartCoroutine(ECloseScene(immediately));
    }

    IEnumerator ECloseScene(bool immediately = false)
    {
        ClearScene(immediately);
        yield return EBackGroundSwitch(immediately ? 0f : BackgroundDuration, false);
        if (scene_camera != null) scene_camera.enabled = false;

        Time.timeScale = 1;
    }

    /// <summary>清掉本次演出生成的图片与文本。</summary>
    public void ClearScene(bool immediately = false)
    {
        for (int i = 0; i < spawnedItems.Count; i++) spawnedItems[i]?.Delete();
        spawnedItems.Clear();
    }

    IEnumerator EBackGroundSwitch(float time, bool on)
    {
        if (BackgroundSprite == null) yield break;

        float BlurEnd = on ? BackgroundBlur : 0;
        float AlphaEnd = on ? BackgroundTransparent : 0;
        Color col = BackgroundSprite.color;
        if (time <= 0)
        {
            if (BackgroundSprite.sharedMaterial != null) BackgroundSprite.sharedMaterial.SetFloat("_Size", BlurEnd);
            col.a = AlphaEnd;
            BackgroundSprite.color = col;
        }
        else
        {
            float BlurStart = BackgroundSprite.sharedMaterial != null ? BackgroundSprite.sharedMaterial.GetFloat("_Size") : 0f;
            float AlphaStart = col.a;
            float t = 0;
            time = Mathf.Max(0.1f, time);
            while (true)
            {
                float curveT = BackgroundCurve.Evaluate(t);
                if (BackgroundSprite.sharedMaterial != null) BackgroundSprite.sharedMaterial.SetFloat("_Size", Mathf.Lerp(BlurStart, BlurEnd, curveT));
                col.a = Mathf.Lerp(AlphaStart, AlphaEnd, curveT);
                BackgroundSprite.color = col;
                if (t >= 1) break;
                yield return WaitForCaptureUpdate();
                t += CaptureDeltaTime / time;
            }
        }
    }

    #endregion

    #region 演出调度

    private readonly List<StoryScene> sceneQueue = new();
    private bool queueRunning;
    private TaskCompletionSource<bool> queueDone;

    /// <summary>
    /// 舞台队列是否还在跑（含最后的关场淡出）。false = 舞台彻底收工：画面还给了战场、timeScale 也放开了。
    /// 战场上的后续演出（升级连线 / 飘字）要等它变 false 再启动，否则 Screen Space Overlay 的 UI
    /// 会直接压在还占着屏幕的舞台上。
    /// </summary>
    public bool IsPerforming => queueRunning;

    /// <summary>把一个演出交给舞台跑。队列空时自动开舞台，全部演完后自动关舞台。</summary>
    public void Play(StoryScene scene)
    {
        if (scene == null || !enableScenes) return;
        sceneQueue.Add(scene);
        if (!queueRunning) StartCoroutine(RunSceneQueue());
    }

    private IEnumerator RunSceneQueue()
    {
        queueRunning = true;
        queueDone = new TaskCompletionSource<bool>();

        if (!IsOpen)
        {
            OpenScene();                                   // 打开舞台（内部会起背景过场）
            yield return WaitStage(BackgroundDuration);     // 等它铺满，不再重复起第二条过场协程
            FitStageToCamera();                             // 过场走完再按**此刻**的视口重铺一次背景
        }

        while (true)
        {
            while (sceneQueue.Count > 0)
            {
                StoryScene scene = sceneQueue[0];
                yield return PlayIsolated(scene);
                sceneQueue.RemoveAt(0);
            }

            CloseScene();
            yield return WaitStage(BackgroundDuration);

            // 淡出这段时间里可能又排了新演出（悄悄话回复和升级常常挨得很近）
            if (sceneQueue.Count == 0) break;
            OpenScene();
            yield return WaitStage(BackgroundDuration);
        }

        queueRunning = false;
        queueDone?.TrySetResult(true);
        queueDone = null;
    }

    /// <summary>
    /// 单独跑一个场景：里面的异常只打死这一个场景，不会把整条队列（连同冻住的战场）一起带走。
    /// 手写 MoveNext 而不是 yield return scene.Play()，就是为了留住这一层 try。
    /// </summary>
    private IEnumerator PlayIsolated(StoryScene scene)
    {
        IEnumerator it = scene.Play();
        if (it == null) yield break;

        while (true)
        {
            object current;
            try
            {
                if (!it.MoveNext()) break;
                current = it.Current;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[StoryTeller] 场景 {scene.GetType().Name} 抛异常，已跳过这个场景：{e}");
                break;
            }
            yield return current;
        }
    }

    #endregion
}
