using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 用一块 Mesh（MeshFilter + MeshRenderer）+ Custom/ContainerMesh 画出「圆角矩形背景 + 墙体环」，
/// 替代原来用 5 个 SpriteRenderer 拼出来的容器图像（原实现 Assets/C#/UI显示/WallEditor.cs，已作废保留）。
/// 整块容器只有 1 个渲染器、1 个 DrawCall、1 个排序号：背景与墙环都在 shader 里按 SDF 算出来。
///
/// 结构：网格挂在子物体 BackgroundOutline 上（缺引用按名字找回，找不到才新建）。
///
/// 约定（与 SpriteMeshDisplay 一致）：
/// * 网格本地坐标 1 单位 = 世界 1 单位，所以该子物体自身缩放会强制为 1，形状尺寸直接烘焙进网格；
/// * 形状（外框尺寸/中心偏移、四边墙厚、四角半径）与颜色都走 MaterialPropertyBlock，共享材质不产生材质实例；
/// * 网格是过程网格（HideFlags.DontSave），启用或改参数时自动重建，所以打包后（网格不序列化）一样显示。
///
/// 尺寸语义与旧 WallEditor 完全一致：
/// 外框尺寸 = (width + 左 + 右, heigth + 上 + 下)，外框中心相对内容中心偏移 ((右-左)/2, (上-下)/2)，
/// 内孔正好是 width×heigth 且以物体原点为中心。
/// </summary>
[ExecuteAlways]
public class ContainerMesh : MonoBehaviour
{
    private const string ShaderName = "Custom/ContainerMesh";
    private const string DefaultMaterialPath = "Material/ContainerMesh";
    private const string ChildName = "BackgroundOutline";
    private const string MeshName = "ContainerQuad";

    [Header("内部大小")]
    public float width = 5f;
    public float heigth = 5f;

    [Header("墙体宽度（x=上 y=右 z=下 w=左，按 CSS margin 顺序）")]
    public Vector4 wall_width = new Vector4(0.1f, 0.1f, 0.1f, 0.1f);

    [Header("圆角半径（x=左上 y=右上 z=右下 w=左下，按 CSS 顺序）")]
    public Vector4 corner_radius = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);

    [Header("颜色")]
    public Color wall_color = Color.white;
    public Color background_color = new Color(0.21960784f, 0.21960784f, 0.21960784f, 1f);

    [Header("渲染顺序（整块容器只有一个排序号）")]
    public int sortingOrder = 2;

    [Header("网格外扩（世界单位）：给圆角边缘留抗锯齿的空间，纯透明不显示")]
    public float edge_padding = 0.25f;

    [Header("材质：留空则用 Resources/Material/ContainerMesh")]
    public Material material;

    [Header("刷新")]
    public bool refresh;

    [Header("子物体（缺引用会按名字找回，找不到才新建）")]
    public Transform BackgroundOutline;

    private static Material _fallbackMaterial;

    private void OnEnable() { Rebuild(); }
    private void OnValidate() { Rebuild(); }

    /// <summary>按当前参数重建网格与渲染参数。Inspector 改参数 / 物体启用时自动调用，也可以右键菜单手动重建。</summary>
    [ContextMenu("重建")]
    public void Rebuild()
    {
        refresh = false;

        Vector4 m = wall_width;   // m.x=上、m.y=右、m.z=下、m.w=左
        Vector2 innerSize = new Vector2(Mathf.Max(width, 0f), Mathf.Max(heigth, 0f));
        Vector2 outerSize = new Vector2(innerSize.x + m.w + m.y, innerSize.y + m.x + m.z);
        Vector2 outerCenter = new Vector2((m.y - m.w) * 0.5f, (m.x - m.z) * 0.5f);

        Vector4 outerRadius = ClampRadii(corner_radius, outerSize);
        Vector4 innerRadius = ClampRadii(InnerRadii(outerRadius, m), innerSize);

        Material mat = ResolveMaterial();

        // 整块容器（背景 + 墙环）都画在这一个子物体上：外框中心偏移烘焙进网格顶点，物体本身留在内容中心
        Transform target = EnsureChild(BackgroundOutline, ChildName);
        if (target == null) return;

        BackgroundOutline = target;
        MeshRenderer meshRenderer = EnsureRenderer(target.gameObject);
        EnsureMesh(meshRenderer, MeshName, outerSize, outerCenter);
        ResetLocalTransform(target);
        SetupRenderer(meshRenderer, mat, sortingOrder);
        SetParams(meshRenderer, outerSize, outerCenter, outerRadius, innerRadius, m);
    }

    // ---------------- 形状参数 ----------------

    /// <summary>四角半径按 CSS 规则收紧：同一条边上两个角之和超过边长时整组等比缩小，避免圆角互相穿插。</summary>
    private static Vector4 ClampRadii(Vector4 r, Vector2 size)
    {
        r = new Vector4(Mathf.Max(r.x, 0f), Mathf.Max(r.y, 0f), Mathf.Max(r.z, 0f), Mathf.Max(r.w, 0f));

        float k = 1f;
        k = Mathf.Min(k, Ratio(size.x, r.x + r.y));   // 上边：左上 + 右上
        k = Mathf.Min(k, Ratio(size.x, r.w + r.z));   // 下边：左下 + 右下
        k = Mathf.Min(k, Ratio(size.y, r.x + r.w));   // 左边：左上 + 左下
        k = Mathf.Min(k, Ratio(size.y, r.y + r.z));   // 右边：右上 + 右下

        return k < 1f ? r * k : r;
    }

    private static float Ratio(float side, float sum)
    {
        if (sum <= 1e-6f) return 1f;
        return Mathf.Clamp01(Mathf.Max(side, 0f) / sum);
    }

    /// <summary>内孔半径 = 外角半径 − 该角相邻两条边里更厚的那条（保证内孔圆角不会顶穿外墙）。</summary>
    private static Vector4 InnerRadii(Vector4 outer, Vector4 m)
    {
        // x=左上、y=右上、z=右下、w=左下
        return new Vector4(
            Mathf.Max(outer.x - Mathf.Max(m.x, m.w), 0f),
            Mathf.Max(outer.y - Mathf.Max(m.x, m.y), 0f),
            Mathf.Max(outer.z - Mathf.Max(m.z, m.y), 0f),
            Mathf.Max(outer.w - Mathf.Max(m.z, m.w), 0f));
    }

    // ---------------- 子物体 / 渲染器 ----------------

    private Transform EnsureChild(Transform current, string childName)
    {
        if (current != null) return current;

        // 引用空了但同级里已经有同名子物体（引用被清空）：先捡回来，避免重复生成
        Transform existing = transform.Find(childName);
        if (existing != null) return existing;

#if UNITY_EDITOR
        // prefab 资产（Project 里的 prefab 本体）身上不允许挂子物体，Unity 会拦住并报错，
        // 这种情况直接不补：等它被实例化/拖进场景时再自动补上
        if (!Application.isPlaying && UnityEditor.PrefabUtility.IsPartOfPrefabAsset(gameObject)) return null;
#endif

        var go = new GameObject(childName);
        go.layer = gameObject.layer;
        go.transform.SetParent(transform, false);
        return go.transform;
    }

    private static void ResetLocalTransform(Transform t)
    {
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
    }

    /// <summary>取（没有就加）MeshFilter + MeshRenderer。</summary>
    private static MeshRenderer EnsureRenderer(GameObject go)
    {
        if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr == null) mr = go.AddComponent<MeshRenderer>();
        return mr;
    }

    private static void SetupRenderer(Renderer r, Material mat, int sortingOrder)
    {
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.sortingOrder = sortingOrder;
    }

    /// <summary>形状与颜色逐物体传给 shader（共享材质 + MPB，不产生材质实例）。</summary>
    private void SetParams(Renderer r, Vector2 outerSize, Vector2 outerCenter, Vector4 outerRadius, Vector4 innerRadius, Vector4 wallWidth)
    {
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor("_WallColor", wall_color);
        mpb.SetColor("_BackColor", background_color);
        mpb.SetVector("_ShapeSize", new Vector4(outerSize.x, outerSize.y, 0f, 0f));
        mpb.SetVector("_ShapeCenter", new Vector4(outerCenter.x, outerCenter.y, 0f, 0f));
        mpb.SetVector("_Radius", outerRadius);
        mpb.SetVector("_RadiusInner", innerRadius);
        mpb.SetVector("_WallWidth", wallWidth);
        r.SetPropertyBlock(mpb);
    }

    // ---------------- 网格 ----------------

    private void EnsureMesh(Renderer r, string meshName, Vector2 size, Vector2 center)
    {
        MeshFilter mf = r.GetComponent<MeshFilter>();
        Mesh mesh = mf.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh { name = meshName, hideFlags = HideFlags.DontSave };
            mf.sharedMesh = mesh;
        }
        else if (mesh.name != meshName)
        {
            mesh.name = meshName;
        }
        FillQuad(mesh, size, center);
    }

    /// <summary>填一张覆盖「size + 四周各 edge_padding」的四边形；center 是形状中心在本地坐标里的位置。</summary>
    private void FillQuad(Mesh mesh, Vector2 size, Vector2 center)
    {
        float pad = Mathf.Max(edge_padding, 0f);
        float hx = size.x * 0.5f + pad;
        float hy = size.y * 0.5f + pad;

        var verts = new Vector3[4];
        var uvs = new Vector2[4];
        float[] sx = { -1f, 1f, 1f, -1f };
        float[] sy = { -1f, -1f, 1f, 1f };

        for (int i = 0; i < 4; i++)
        {
            float x = center.x + sx[i] * hx;
            float y = center.y + sy[i] * hy;
            verts[i] = new Vector3(x, y, 0f);
            uvs[i] = new Vector2((sx[i] + 1f) * 0.5f, (sy[i] + 1f) * 0.5f);
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
    }

    // ---------------- 材质 ----------------

    /// <summary>材质：优先用面板上的引用，其次 Resources（真资产，能存进 prefab），最后才临时创建。</summary>
    private Material ResolveMaterial()
    {
        if (material != null) return material;

        Material fromResources = Resources.Load<Material>(DefaultMaterialPath);
        if (fromResources != null) return fromResources;

        if (_fallbackMaterial != null) return _fallbackMaterial;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[ContainerMesh] Resources 里没有“{DefaultMaterialPath}”，也找不到 shader“{ShaderName}”。");
            return null;
        }

        _fallbackMaterial = new Material(shader) { hideFlags = HideFlags.DontSave, name = "ContainerMeshFallback" };
        return _fallbackMaterial;
    }
}
