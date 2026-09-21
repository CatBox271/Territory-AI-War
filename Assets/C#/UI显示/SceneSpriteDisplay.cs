using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 用 Mesh（MeshFilter + MeshRenderer）来显示 sprite 的测试组件：**一个 sprite = 一个子物体 = 一张单四边形 Mesh**。
///
/// 为什么要这样做：SpriteRenderer 的网格是 Unity 生成的（紧贴图形边缘、不能改顶点），所以图形之外没有像素，
/// shader 再厉害也没地方画外描边/挖空。这里每个 sprite 生成一张“比 sprite 大一圈 padding”的四边形，
/// 于是 Custom/SpriteMeshOutline（只做描边）与 Custom/SpriteMeshDot（网点渐变＝透明）就有空间在图形之外作画。
///
/// 约定：
/// * 网格本地坐标 1 单位 = 世界 1 单位（sprite 世界尺寸 = sprite.rect.size / pixelsPerUnit），请让对象自身缩放保持 1；
///   形状运算都按世界单位算，所以孔是正圆、描边等宽，不会像 texel 描边那样被拉伸成各向异性。
/// * 每个子物体的贴图/uvRect/尺寸通过 MaterialPropertyBlock 传进 shader；材质默认**每个对象一份实例**（instanceMaterial，可关）。
/// * 网格用 HideFlags.DontSave 且能复用，不污染工程；改参数或重开时自动重建。
/// </summary>
[ExecuteAlways]
public class SceneSpriteDisplay : MonoBehaviour
{
    [Header("Sprite（一个组件只显示这一个）")]
    public Sprite sprite;

    [Tooltip("留空则用内置的 Custom/SpriteMeshDot 材质（运行时创建，不写盘）")]
    public Material material;

    [Tooltip("每个对象用自己的一份材质实例（默认开）：改 shader 参数只影响自己，不会全局串味。" +
             "关掉则所有对象共用同一份材质，只靠 MaterialPropertyBlock 传逐对象参数")]
    public bool instanceMaterial = true;

    [Tooltip("显示框尺寸（世界单位）：舞台写进来的权威尺寸。填了就以它为准，不再看 ContainerMesh 上的 width/heigth" +
             "（那两个值会被别的系统改，改完这里不同步，立绘就会缩成一小团或者只剩中间一块）")]
    public Vector2 frameSize = Vector2.zero;

    [Tooltip("向外扩：网格尺寸 = 内部范围 + expand（每轴各加 expand）。这一圈是特效空间，描边/网点画这里，不会被裁")]
    public float expand = 0f;

    [Tooltip("向内收：sprite 实际显示区 = 内部范围 − padding（每轴各减 padding）")]
    public float padding = 0f;

    [Tooltip("等比：sprite 保持原图长宽比缩进显示区（会留边）；关掉则拉伸填满显示区")]
    public bool keepAspect = true;

    [Tooltip("显示区放大倍率：立绘原图四周常有透明留白，1.2~1.5 可以把人物顶到框里。>1 时网格会一起放大，不会被四边形裁掉")]
    public float zoom = 1f;

    [Header("整体可见度（网点口径：可见面积比例 0~1）")]
    [Tooltip("整个 sprite 的可见面积比例，对应 shader 的 _Alpha。1 = 原图；越小网点越稀、越透")]
    [Range(0f, 1f)] public float visibility = 1f;

    [Header("四边渐变（x=开始比例, y=结束比例, z=开始透明度, w=结束透明度）")]
    [Tooltip("比例 = 从这条边到对边的归一化距离：0 = 就在这条边上，1 = 到了对边。默认 (0,0,1,1) = 不渐变")]
    public Vector4 edgeTop = new Vector4(0f, 0f, 1f, 1f);
    public Vector4 edgeRight = new Vector4(0f, 0f, 1f, 1f);
    public Vector4 edgeBottom = new Vector4(0f, 0f, 1f, 1f);
    public Vector4 edgeLeft = new Vector4(0f, 0f, 1f, 1f);

    [Header("四边渐变·贝塞尔中间点（x=在渐变区间里的比例 0~1, y=该点的透明度；y < 0 = 线性）")]
    [Tooltip("在 (开始比例→结束比例) 这段区间里插一个控制点，透明度按二次贝塞尔走 —— 可以做「慢起快落」这类非线性渐变。\n" +
             "x 是**区间内**的位置：0 = 开始比例那一点，1 = 结束比例那一点。\n" +
             "y 正好等于起止连线上对应值时为纯线性；y 比它小/大就是往透明/不透明凹。留 y = -1 表示不插控制点（线性）。")]
    public Vector2 edgeTopMid = new Vector2(0.5f, -1f);
    public Vector2 edgeRightMid = new Vector2(0.5f, -1f);
    public Vector2 edgeBottomMid = new Vector2(0.5f, -1f);
    public Vector2 edgeLeftMid = new Vector2(0.5f, -1f);

    [Tooltip("整体颜色（逐个 sprite 通过 MaterialPropertyBlock 传给 _Color）")]
    public Color color = Color.white;

    [Header("渲染")]
    public string sortingLayer = "Default";
    public int sortingOrder = 0;

    [Header("刷新")]
    [Tooltip("勾一下强制重建；参数变化时也会自动重建")]
    public bool rebuild;

    private const string ChildPrefix = "SM_";
    private const string ShaderName = "Custom/SpriteMeshDefault";
    private const string DefaultMaterialResourcePath = "Material/SpriteMeshDefault";
    private static Material _fallbackMaterial;

    private void OnEnable() { Rebuild(); }
    private void OnValidate() { Rebuild(); }

    public bool keepRebuild = false;
    private void LateUpdate()
    {
        if (keepRebuild) Rebuild();
    }

    [ContextMenu("重建")]


    public void Rebuild()
    {
        rebuild = false;
        SyncChildren();
    }

    /// <summary>
    /// 一次性配置：sprite 与 material 都按 Resources 路径加载（不含扩展名），
    /// 并设置 expand / padding / 颜色 / 是否保持比例，然后重建。
    /// </summary>
    public void Set(string spriteResourcePath, string materialResourcePath, float expand, float padding, Color color, bool keepAspect)
    {
        sprite = string.IsNullOrEmpty(spriteResourcePath) ? null : Resources.Load<Sprite>(spriteResourcePath);
        material = string.IsNullOrEmpty(materialResourcePath) ? null : Resources.Load<Material>(materialResourcePath);
        this.expand = expand;
        this.padding = padding;
        this.color = color;
        this.keepAspect = keepAspect;
        Rebuild();
    }

    /// <summary>直接设置材质（不走 Resources），然后重建。</summary>
    public void SetMaterial(Material material)
    {
        this.material = material;
        Rebuild();
    }

    // ---------------- 子物体同步 ----------------

    private void SyncChildren()
    {
        // 自己创建的 SM_ 前缀子物体（正常只会有 1 个）
        var existing = new List<Transform>();
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform c = transform.GetChild(i);
            if (c != null && c.name.StartsWith(ChildPrefix)) existing.Add(c);
        }

        if (sprite == null)
        {
            // 不销毁、只隐藏：编辑期（OnValidate / Prefab 内容）销毁会撞 Unity 的限制，隐藏则到处都能用
            foreach (Transform c in existing) c.gameObject.SetActive(false);
            return;
        }

        Transform child = existing.Count > 0 ? existing[0] : null;
        for (int i = 1; i < existing.Count; i++) existing[i].gameObject.SetActive(false);   // 多余的隐藏

        if (child == null)
        {
            var go = new GameObject(ChildPrefix + sprite.name);
            go.transform.SetParent(transform, false);
            child = go.transform;
        }
        child.gameObject.SetActive(true);
        ApplyToChild(child, sprite);
    }

    private void ApplyToChild(Transform child, Sprite sprite)
    {
        child.name = ChildPrefix + sprite.name;

        // 网格顶点是按世界单位烘焙的（1 单位 = 1 世界单位），子物体自己带缩放/偏移就会把尺寸算歪：
        // 实例化时从 prefab 继承下来的旧缩放会让整幅立绘缩成一小团。这里每次都归零。
        child.localPosition = Vector3.zero;
        child.localRotation = Quaternion.identity;
        child.localScale = Vector3.one;

        MeshFilter mf = child.GetComponent<MeshFilter>();
        if (mf == null) mf = child.gameObject.AddComponent<MeshFilter>();
        MeshRenderer mr = child.GetComponent<MeshRenderer>();
        if (mr == null) mr = child.gameObject.AddComponent<MeshRenderer>();

        // 1) 几何：网格 = 内部范围 + expand；sprite 只画在显示区（内部范围 − padding）里
        Vector2 meshSize = GetMeshSize();
        Vector2 drawnSize = GetDrawnSize(sprite);

        meshSize = new Vector2(Mathf.Max(meshSize.x, drawnSize.x), Mathf.Max(meshSize.y, drawnSize.y));

        // 一次性自检：**每个对象各打一条**（原来是 static，编辑器里 prefab 的 OnValidate 先把名额用掉了，
        // 运行时反而一条都不打）。框 / 网格 / 内容区三个尺寸对不对，一眼就能看出来。
        if (!loggedOnce)
        {
            loggedOnce = true;
        }

        Vector4 contentRect = new Vector4(-drawnSize.x * 0.5f, -drawnSize.y * 0.5f, drawnSize.x * 0.5f, drawnSize.y * 0.5f);

        Mesh mesh = mf.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh { name = "SpriteMeshQuad", hideFlags = HideFlags.DontSave };
            mf.sharedMesh = mesh;
        }
        FillQuad(mesh, sprite, meshSize, contentRect);

        // 2) 材质 + 逐个 sprite 的参数
        Material source = material != null ? material : EnsureFallbackMaterial();
        Material runtimeMaterial = ResolveInstanceMaterial(source);
        mr.sharedMaterial = runtimeMaterial;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.sortingLayerName = sortingLayer;
        mr.sortingOrder = sortingOrder;

        _mpb ??= new MaterialPropertyBlock();
        _mpb.Clear();
        _mpb.SetTexture("_MainTex", sprite.texture);
        _mpb.SetVector("_SpriteRect", GetSpriteUvRect(sprite));
        _mpb.SetVector("_ContentRect", contentRect);
        _mpb.SetFloat("_Alpha", Mathf.Clamp01(visibility));
        _mpb.SetVector("_EdgeTop", edgeTop);
        _mpb.SetVector("_EdgeRight", edgeRight);
        _mpb.SetVector("_EdgeBottom", edgeBottom);
        _mpb.SetVector("_EdgeLeft", edgeLeft);
        _mpb.SetVector("_EdgeTopMid", edgeTopMid);
        _mpb.SetVector("_EdgeRightMid", edgeRightMid);
        _mpb.SetVector("_EdgeBottomMid", edgeBottomMid);
        _mpb.SetVector("_EdgeLeftMid", edgeLeftMid);
        _mpb.SetColor("_Color", color);
        mr.SetPropertyBlock(_mpb);

        // 材质实例直接写一份完整参数：即使 MPB 被回读/清空，每个对象仍持有自己的 _Alpha/_ContentRect/贴图，不会串回共享材质默认值
        if (runtimeMaterial != source && runtimeMaterial != null)
        {
            runtimeMaterial.SetTexture("_MainTex", sprite.texture);
            runtimeMaterial.SetVector("_SpriteRect", GetSpriteUvRect(sprite));
            runtimeMaterial.SetVector("_ContentRect", contentRect);
            runtimeMaterial.SetFloat("_Alpha", Mathf.Clamp01(visibility));
            runtimeMaterial.SetVector("_EdgeTop", edgeTop);
            runtimeMaterial.SetVector("_EdgeRight", edgeRight);
            runtimeMaterial.SetVector("_EdgeBottom", edgeBottom);
            runtimeMaterial.SetVector("_EdgeLeft", edgeLeft);
            runtimeMaterial.SetVector("_EdgeTopMid", edgeTopMid);
            runtimeMaterial.SetVector("_EdgeRightMid", edgeRightMid);
            runtimeMaterial.SetVector("_EdgeBottomMid", edgeBottomMid);
            runtimeMaterial.SetVector("_EdgeLeftMid", edgeLeftMid);
            runtimeMaterial.SetColor("_Color", color);
        }
        _renderer = mr;
    }

    /// <summary>网格尺寸 = 内部范围 × zoom + expand（每轴各加 expand）。多出来的一圈就是特效空间。</summary>
    public Vector2 GetMeshSize()
    {
        Vector2 frame = GetFrameSize() * Mathf.Max(0.01f, zoom);
        return new Vector2(frame.x + expand, frame.y + expand);
    }

    /// <summary>sprite 的实际显示区 = (内部范围 − padding) × zoom。</summary>
    public Vector2 GetDisplaySize()
    {
        Vector2 frame = GetFrameSize();
        if (frame.x <= 0f || frame.y <= 0f) return Vector2.zero;
        float k = Mathf.Max(0.01f, zoom);
        return new Vector2(Mathf.Max(frame.x - padding, 0.0001f) * k, Mathf.Max(frame.y - padding, 0.0001f) * k);
    }

    /// <summary>sprite 真正被画出来的尺寸：keepAspect 时按显示区等比贴合，否则拉伸填满显示区。</summary>
    public Vector2 GetDrawnSize(Sprite sprite)
    {
        Vector2 spriteSize = GetSpriteWorldSize(sprite);
        if (spriteSize.x <= 0f || spriteSize.y <= 0f) return spriteSize;

        Vector2 display = GetDisplaySize();
        if (display.x <= 0f || display.y <= 0f) return spriteSize;   // 找不到框就按原图尺寸

        if (!keepAspect) return display;
        float k = Mathf.Min(display.x / spriteSize.x, display.y / spriteSize.y);
        return spriteSize * k;
    }

    /// <summary>容器框尺寸：ContainerMesh 可能在自己身上、也可能在子物体或父物体上（脚本常挂在容器上一层）。</summary>
    public Vector2 GetFrameSize()
    {

        ContainerMesh frame = FrameContainer;
        Vector2 size = frame != null ? new Vector2(frame.width, frame.heigth) : Vector2.zero;
        if (size.x > 0f && size.y > 0f) return size;
        // 尺寸是 0（ContainerMesh 缺失、或者它自己的 width/heigth 就是 0）时，
        // 退回 SetFrame 记下的期望尺寸。退回 0 的话网格只剩 expand 那一圈、
        // 显示区被算成「sprite 原尺寸」，画面就是「立绘只剩中间一小块被放大」。
        if (frameFallback.x > 0f && frameFallback.y > 0f) return frameFallback;
        return size;
    }

    /// <summary>SetFrame 传进来的期望尺寸（容器框缺失 / 尺寸为 0 时拿它兜底）。</summary>
    private Vector2 frameFallback = Vector2.zero;

    /// <summary>自检日志是否已经打过（**每个对象各一条**：static 会被编辑器里 prefab 的 OnValidate 先占掉，运行时就不打了）。</summary>
    private bool loggedOnce;

    /// <summary>容器框：ContainerMesh 可能在自己身上、也可能在子物体或父物体上。</summary>
    public ContainerMesh FrameContainer
    {
        get
        {
            if (_container != null) return _container;
            _container = GetComponent<ContainerMesh>();
            if (_container == null) _container = GetComponentInChildren<ContainerMesh>(true);
            if (_container == null) _container = GetComponentInParent<ContainerMesh>(true);
            return _container;
        }
    }

    /// <summary>设置容器框尺寸与配色（找不到 ContainerMesh 时尺寸仍然记下来给网格兜底用）。</summary>
    public void SetFrame(Vector2 frameSize, float cornerRadius, float wallWidth, Color wallColor, Color backgroundColor, int sortingOrder = -1)
    {

        frameFallback = frameSize;
        ContainerMesh frame = FrameContainer;
        if (frame == null)
        {
            Rebuild();   // 没容器：尺寸也变了，图必须自己重画一次
            return;
        }
        if (frameSize != Vector2.zero)
        {
            frame.width = frameSize.x;
            frame.heigth = frameSize.y;
        }
        frame.corner_radius = Vector4.one * cornerRadius;
        frame.wall_width = Vector4.one * wallWidth;
        frame.wall_color = wallColor;
        frame.background_color = backgroundColor;
        if (sortingOrder >= 0) frame.sortingOrder = sortingOrder;
        Rebuild();       // 框刷完必须把图也刷一遍：四边形 / 内容区都是按这个框算的
        frame.Rebuild();
    }

    /// <summary>
    /// 整体透明度：sprite 自身的 _Color.a + **容器框**（ContainerMesh）的 alpha 一起改。
    /// 容器不跟着淡的话，图片卡的底板会整块「啪」一下出现、出场时文字淡走了底板还留在原地。
    /// 只写 MPB / 容器颜色，不重建网格，演出里每帧调也安全。
    /// </summary>
    public void SetAlpha(float a)
    {
        a = Mathf.Clamp01(a);
        if (Mathf.Abs(_lastAlpha - a) < 0.001f) return;
        _lastAlpha = a;

        color.a = a;
        FrameContainer?.SetAlpha(a);

        if (_renderer == null || _mpb == null) return;
        // 关键：不要 GetPropertyBlock 回读。_mpb 在 ApplyToChild 里已经装好 _MainTex/_SpriteRect/_ContentRect/_Alpha/边缘参数，
        // 回读渲染器块一旦为空/旧，就会把这些参数全部冲掉，材质退回 SpriteMeshDot 的默认值（_Alpha=0.215），立绘就显示不全。
        _mpb.SetColor("_Color", color);
        if (_instance != null) _instance.SetColor("_Color", color);   // 实例材质同步刷新，不依赖 MPB
        _renderer.SetPropertyBlock(_mpb);
    }
    private float _lastAlpha = 1f;

    private Material _instance;
    private Material _instanceSource;

    /// <summary>
    /// 取这个对象自己用的材质。默认**每个对象一份实例**：改 shader 参数只影响自己，不会全局串味。
    /// 关掉 instanceMaterial 就退回"共用同一份"（只靠 MaterialPropertyBlock 传逐对象参数）。
    /// 只在真实场景里建实例，prefab 资产 / 编辑期不建，免得往工程里写垃圾。
    /// </summary>
    private Material ResolveInstanceMaterial(Material source)
    {
        if (source == null || !instanceMaterial) return source;
        if (!gameObject.scene.IsValid()) return source;

        if (_instance == null || _instanceSource != source)
        {
            ReleaseInstance();
            _instance = new Material(source)
            {
                hideFlags = HideFlags.DontSave,
                name = source.name + " (Instance)"
            };
            _instanceSource = source;
        }
        return _instance;
    }

    private void ReleaseInstance()
    {
        if (_instance == null) return;
        if (Application.isPlaying) Destroy(_instance);
        else DestroyImmediate(_instance);
        _instance = null;
        _instanceSource = null;
    }

    private void OnDestroy()
    {
        ReleaseInstance();
    }

    private MaterialPropertyBlock _mpb;
    private Renderer _renderer;
    private ContainerMesh _container;

    // ---------------- 网格 / 贴图 ----------------

    /// <summary>sprite 的世界尺寸 = rect 尺寸 / pixelsPerUnit（与 SpriteRenderer 的默认缩放一致）。</summary>
    public static Vector2 GetSpriteWorldSize(Sprite sprite)
    {
        if (sprite == null) return Vector2.zero;
        float ppu = sprite.pixelsPerUnit > 0f ? sprite.pixelsPerUnit : 100f;
        return new Vector2(sprite.rect.width / ppu, sprite.rect.height / ppu);
    }

    /// <summary>sprite 在贴图里的 uv 矩形：(uvMin.x, uvMin.y, uvSize.x, uvSize.y)。</summary>
    public static Vector4 GetSpriteUvRect(Sprite sprite)
    {
        if (sprite == null) return new Vector4(0f, 0f, 1f, 1f);
        float tw = Mathf.Max(1f, sprite.texture.width);
        float th = Mathf.Max(1f, sprite.texture.height);
        return new Vector4(sprite.rect.xMin / tw, sprite.rect.yMin / th, sprite.rect.width / tw, sprite.rect.height / th);
    }

    /// <summary>
    /// 填充四边形：本地坐标 1 单位 = 世界 1 单位。
    /// uv 把 sprite 的 rect 映射到 contentRect（sprite 实际显示区）上，区域外按比例外推（贴图 wrap 为 Clamp 时不会串图）；
    /// 这样即使换成普通 sprite 材质，也能看到 sprite 本体。绕序无所谓（shader 是 Cull Off）。
    /// </summary>
    private static void FillQuad(Mesh mesh, Sprite sprite, Vector2 meshSize, Vector4 contentRect)
    {
        float hx = meshSize.x * 0.5f, hy = meshSize.y * 0.5f;
        float cw = Mathf.Max(contentRect.z - contentRect.x, 1e-5f);
        float ch = Mathf.Max(contentRect.w - contentRect.y, 1e-5f);

        Vector4 uvRect = GetSpriteUvRect(sprite);
        Vector2 uvMin = new Vector2(uvRect.x, uvRect.y);
        Vector2 uvSize = new Vector2(uvRect.z, uvRect.w);

        var verts = new Vector3[4];
        var uvs = new Vector2[4];
        Vector3[] corners =
        {
            new Vector3(-hx, -hy, 0f),
            new Vector3( hx, -hy, 0f),
            new Vector3( hx,  hy, 0f),
            new Vector3(-hx,  hy, 0f)
        };

        for (int i = 0; i < 4; i++)
        {
            verts[i] = corners[i];
            float tx = (corners[i].x - contentRect.x) / cw;
            float ty = (corners[i].y - contentRect.y) / ch;
            uvs[i] = uvMin + new Vector2(tx * uvSize.x, ty * uvSize.y);
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
    }

    // ---------------- 工具 ----------------

    /// <summary>material 字段为空时的默认材质：优先按名字从 Resources 加载（真资产，能正常存进 prefab），加载不到才临时创建。</summary>
    private static Material EnsureFallbackMaterial()
    {
        // 先试 Resources（Unity 内部有缓存，开销极小），保证资源挪动/重新导入后立刻拿到，不被旧缓存挡住
        Material fromResources = Resources.Load<Material>(DefaultMaterialResourcePath);
        if (fromResources != null) return fromResources;

        if (_fallbackMaterial != null) return _fallbackMaterial;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[SpriteMeshDisplay] Resources 里没有 “{DefaultMaterialResourcePath}”，也找不到 shader “{ShaderName}”。");
            return null;
        }

        _fallbackMaterial = new Material(shader) { hideFlags = HideFlags.DontSave, name = "SpriteMeshFallback" };
        return _fallbackMaterial;
    }
}
