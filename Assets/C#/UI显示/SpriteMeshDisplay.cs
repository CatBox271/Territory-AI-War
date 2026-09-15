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
/// * 每个子物体的贴图/uvRect/尺寸通过 MaterialPropertyBlock 传进 shader → 一份共享材质画所有 sprite，不产生材质实例。
/// * 网格用 HideFlags.DontSave 且能复用，不污染工程；改参数或重开时自动重建。
/// </summary>
[ExecuteAlways]
public class SpriteMeshDisplay : MonoBehaviour
{
    [Header("Sprite（一个组件只显示这一个）")]
    public Sprite sprite;

    [Tooltip("留空则用内置的 Custom/SpriteMeshDot 材质（运行时创建，不写盘）")]
    public Material material;

    [Tooltip("向外扩：网格尺寸 = 内部范围 + expand（每轴各加 expand）。这一圈是特效空间，描边/网点画这里，不会被裁")]
    public float expand = 0f;

    [Tooltip("向内收：sprite 实际显示区 = 内部范围 − padding（每轴各减 padding）")]
    public float padding = 0f;

    [Tooltip("等比：sprite 保持原图长宽比缩进显示区（会留边）；关掉则拉伸填满显示区")]
    public bool keepAspect = true;

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

        MeshFilter mf = child.GetComponent<MeshFilter>();
        if (mf == null) mf = child.gameObject.AddComponent<MeshFilter>();
        MeshRenderer mr = child.GetComponent<MeshRenderer>();
        if (mr == null) mr = child.gameObject.AddComponent<MeshRenderer>();

        // 1) 几何：网格 = 内部范围 + expand；sprite 只画在显示区（内部范围 − padding）里
        Vector2 meshSize = GetMeshSize();
        Vector2 drawnSize = GetDrawnSize(sprite);
        Vector4 contentRect = new Vector4(-drawnSize.x * 0.5f, -drawnSize.y * 0.5f, drawnSize.x * 0.5f, drawnSize.y * 0.5f);

        Mesh mesh = mf.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh { name = "SpriteMeshQuad", hideFlags = HideFlags.DontSave };
            mf.sharedMesh = mesh;
        }
        FillQuad(mesh, sprite, meshSize, contentRect);

        // 2) 材质 + 逐个 sprite 的参数（共享材质 + MPB，不产生材质实例）
        mr.sharedMaterial = material != null ? material : EnsureFallbackMaterial();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        mr.sortingLayerName = sortingLayer;
        mr.sortingOrder = sortingOrder;

        var mpb = new MaterialPropertyBlock();
        mpb.SetTexture("_MainTex", sprite.texture);
        mpb.SetVector("_SpriteRect", GetSpriteUvRect(sprite));
        mpb.SetVector("_ContentRect", contentRect);
        mpb.SetColor("_Color", color);
        mr.SetPropertyBlock(mpb);
    }

    /// <summary>网格尺寸 = 内部范围 + expand（每轴各加 expand）。多出来的一圈就是特效空间。</summary>
    public Vector2 GetMeshSize()
    {
        Vector2 frame = GetFrameSize();
        return new Vector2(frame.x + expand, frame.y + expand);
    }

    /// <summary>sprite 的实际显示区 = 内部范围 − padding（每轴各减 padding）。</summary>
    public Vector2 GetDisplaySize()
    {
        Vector2 frame = GetFrameSize();
        if (frame.x <= 0f || frame.y <= 0f) return Vector2.zero;
        return new Vector2(Mathf.Max(frame.x - padding, 0.0001f), Mathf.Max(frame.y - padding, 0.0001f));
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

    /// <summary>容器框尺寸：WallEditor 可能在自己身上、也可能在子物体或父物体上（脚本常挂在容器上一层）。</summary>
    public Vector2 GetFrameSize()
    {
        WallEditor frame = GetComponent<WallEditor>();
        if (frame == null) frame = GetComponentInChildren<WallEditor>(true);
        if (frame == null) frame = GetComponentInParent<WallEditor>(true);
        return frame != null ? new Vector2(frame.width, frame.heigth) : Vector2.zero;
    }

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
