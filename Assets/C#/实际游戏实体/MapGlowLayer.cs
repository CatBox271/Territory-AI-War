using UnityEngine;

/// <summary>
/// 炫光层：地图上盖一个大 quad，把所有登记中的 MapGlow 一次画完 —— 全部炫光共用 1 个 draw call。
///
/// 每个炫光的剖面来自它自己的 AnimationCurve：这里把曲线烤成一张 LUT 贴图（LutCols 列 × MaxGlows 行，一行一个炫光），
/// 曲线没变就不重烤，采样交给 shader。纹理会顺手传给材质。
///
/// 不用手动摆场景：第一个 MapGlow 启用时会自己建一个，也可以自己往场景里放一个来调整层参数。
/// 上限见 MaxGlows（要更多就跟 shader 里的 MAX_GLOWS 一起改）。
/// </summary>
[DisallowMultipleComponent]
public class MapGlowLayer : MonoBehaviour
{
    /// <summary>最多同时画多少个炫光；改这里要同时改 shader 里的 MAX_GLOWS。</summary>
    public const int MaxGlows = 32;

    /// <summary>每个炫光的曲线烤多少个采样点。</summary>
    public const int LutCols = 64;

    public static MapGlowLayer Instance { get; private set; }

    [Header("层级")]
    [Tooltip("相对地图画布往前多少（越小越靠镜头）")]
    public float zOffset = -0.06f;
    [Tooltip("勾上 = 盖在所有单位之上；默认只盖地图、在单位之下")]
    public bool onTop = false;
    [Tooltip("四周多留多少世界单位，免得贴边的炫光被 quad 裁掉")]
    public float sizePadding = 2f;

    [Header("调试")]
    [Tooltip("超过上限时只画前 MaxGlows 个，并在 Console 提示一次")]
    public bool warnWhenOverflow = true;

    const string ShaderName = "Custom/MapGlow";

    static readonly int GlowAID = Shader.PropertyToID("_GlowA");
    static readonly int GlowBID = Shader.PropertyToID("_GlowB");
    static readonly int GlowCID = Shader.PropertyToID("_GlowC");
    static readonly int GlowDID = Shader.PropertyToID("_GlowD");
    static readonly int GlowCountID = Shader.PropertyToID("_GlowCount");
    static readonly int QuadSizeID = Shader.PropertyToID("_QuadSize");
    static readonly int MapTexID = Shader.PropertyToID("_MapTex");
    static readonly int MapSizeID = Shader.PropertyToID("_MapSize");
    static readonly int LutID = Shader.PropertyToID("_GlowLUT");
    static readonly int LutRowsID = Shader.PropertyToID("_LutRows");
    static readonly int LutColsID = Shader.PropertyToID("_LutCols");

    static Shader cachedShader;

    static Shader GlowShader
    {
        get
        {
            if (cachedShader == null)
            {
                cachedShader = Shader.Find(ShaderName);
                if (cachedShader == null) Debug.LogError($"[MapGlowLayer] 找不到 Shader「{ShaderName}」");
            }
            return cachedShader;
        }
    }

    readonly Vector4[] arrA = new Vector4[MaxGlows];
    readonly Vector4[] arrB = new Vector4[MaxGlows];
    readonly Vector4[] arrC = new Vector4[MaxGlows];
    readonly Vector4[] arrD = new Vector4[MaxGlows];
    readonly MapGlow[] slotOwner = new MapGlow[MaxGlows];
    readonly int[] slotHash = new int[MaxGlows];

    Texture2D lut;
    Color[] lutPixels;
    bool lutDirty;
    Texture2D whiteTex;

    Transform quad;
    MeshRenderer quadRenderer;
    Material mat;
    Transform canvasRoot;
    bool overflowWarned;

    /// <summary>拿单例，没有就建一个（MapGlow 登记时自动来）。编辑期不偷偷建物体。</summary>
    public static MapGlowLayer Ensure()
    {
        if (Instance != null) return Instance;
        if (!Application.isPlaying) return null;
        var go = new GameObject("MapGlowLayer");
        return go.AddComponent<MapGlowLayer>();
    }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) { Destroy(this); return; }

        canvasRoot = TerritoryCanvas.Instance != null ? TerritoryCanvas.Instance.transform : null;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (quad != null) { Kill(quad.gameObject); quad = null; quadRenderer = null; }
        if (mat != null) { Kill(mat); mat = null; }
        if (lut != null) { Kill(lut); lut = null; }
        if (whiteTex != null) { Kill(whiteTex); whiteTex = null; }
    }

    void LateUpdate()
    {
        var list = MapGlow.Active;

        if (canvasRoot == null && TerritoryCanvas.Instance != null)
            canvasRoot = TerritoryCanvas.Instance.transform;

        if (list.Count > 0) EnsureLut();

        float mapSize = MapConfig.Instance != null ? MapConfig.Instance.worldSize : 10f;
        float size = Mathf.Max(0.01f, mapSize + sizePadding * 2f);
        Vector3 center = canvasRoot != null ? canvasRoot.position : Vector3.zero;

        int n = 0;
        int total = 0;
        int count = list.Count;
        for (int i = 0; i < count; i++)
        {
            if (i >= list.Count) break;          // 遍历中有人增删也不会越界
            var g = list[i];
            if (g == null || !g.visible) continue;

            total++;
            if (n >= MaxGlows) continue;         // 超出的不画

            Vector3 p = g.WorldPosition;
            float s = g.SizeScale;                                   // 尺寸倍率：勾了 followScale 才是本物体的缩放，否则 1
            float sw = Mathf.Max(0.0005f, g.softness * s);
            float inner = Mathf.Max(0.0005f, g.thickness * s);
            float ring = g.shape == MapGlow.GlowShape.环形 ? Mathf.Max(0f, g.radius * s) : 0f;
            float k = Mathf.Max(0f, g.intensity) * MapGlow.GlobalIntensity * Mathf.Max(0f, g.color.a);

            arrA[n] = new Vector4(p.x - center.x, p.y - center.y, ring, sw);
            arrB[n] = new Vector4(inner, g.color.r * k, g.color.g * k, g.color.b * k);
            arrC[n] = new Vector4(g.metal, g.shade, g.sheen, g.sheenCount);
            arrD[n] = new Vector4(g.sheenSpeed, g.rim, g.rimWidth, g.reflectOffset);

            // 曲线变了才重烤这一行
            int h = g.CurveHash();
            if (slotOwner[n] != g || slotHash[n] != h)
            {
                slotOwner[n] = g;
                slotHash[n] = h;
                BakeRow(n, g.Curve);
                lutDirty = true;
            }

            n++;
        }

        if (warnWhenOverflow && !overflowWarned && total > MaxGlows)
        {
            overflowWarned = true;
            Debug.LogWarning($"[MapGlowLayer] 炫光数量 {total} 超过上限 {MaxGlows}，多余的没画；要更多就把 MapGlowLayer.MaxGlows 和 shader 里的 MAX_GLOWS 一起调大。");
        }

        if (lutDirty && lut != null)
        {
            lut.SetPixels(lutPixels);
            lut.Apply(false);
            lutDirty = false;
        }

        if (n == 0)
        {
            if (quadRenderer != null && quadRenderer.enabled) quadRenderer.enabled = false;
            return;
        }

        EnsureQuad();
        if (quad == null) return;

        quad.position = new Vector3(center.x, center.y, center.z + zOffset);
        quad.localScale = new Vector3(size, size, 1f);

        mat.SetVectorArray(GlowAID, arrA);
        mat.SetVectorArray(GlowBID, arrB);
        mat.SetVectorArray(GlowCID, arrC);
        mat.SetVectorArray(GlowDID, arrD);
        mat.SetInt(GlowCountID, n);
        mat.SetFloat(QuadSizeID, size);
        mat.SetFloat(MapSizeID, mapSize);
        mat.SetTexture(MapTexID, MapTexture());
        mat.renderQueue = onTop ? 3001 : 2999;

        quadRenderer.enabled = true;
    }

    /// <summary>把一条剖面曲线烤进 LUT 的第 row 行（x 从 0 到 1 均匀取 LutCols 个点）。</summary>
    void BakeRow(int row, AnimationCurve curve)
    {
        int b = row * LutCols;
        for (int x = 0; x < LutCols; x++)
        {
            float v = curve.Evaluate((float)x / (LutCols - 1));
            lutPixels[b + x] = new Color(v > 0f ? v : 0f, 0f, 0f, 1f);
        }
    }

    void EnsureLut()
    {
        if (lut != null) return;

        var fmt = SystemInfo.SupportsTextureFormat(TextureFormat.RFloat) ? TextureFormat.RFloat : TextureFormat.ARGB32;
        lut = new Texture2D(LutCols, MaxGlows, fmt, false);
        lut.name = "MapGlowLUT";
        lut.filterMode = FilterMode.Bilinear;
        lut.wrapMode = TextureWrapMode.Clamp;
        lut.hideFlags = HideFlags.HideAndDontSave;

        lutPixels = new Color[LutCols * MaxGlows];
        for (int i = 0; i < lutPixels.Length; i++) lutPixels[i] = new Color(0f, 0f, 0f, 1f);
        lut.SetPixels(lutPixels);
        lut.Apply(false);
    }

    void EnsureQuad()
    {
        if (quad != null) return;
        var shader = GlowShader;
        if (shader == null) return;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "MapGlowQuad";
        var box = go.GetComponent<Collider>();
        if (box != null) Kill(box);

        quad = go.transform;
        quad.SetParent(canvasRoot, false);

        quadRenderer = go.GetComponent<MeshRenderer>();
        quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        quadRenderer.receiveShadows = false;
        quadRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        quadRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        mat = new Material(shader);
        mat.name = "MapGlowLayer(Instance)";
        mat.SetTexture(LutID, lut);
        mat.SetFloat(LutRowsID, MaxGlows);
        mat.SetFloat(LutColsID, LutCols);
        quadRenderer.sharedMaterial = mat;
    }

    /// <summary>背后的地图：优先用 TerritoryCanvas 的显示图（displayRT），没有就给一张白图（等于不调制）。</summary>
    Texture MapTexture()
    {
        var canvas = TerritoryCanvas.Instance;
        if (canvas != null && canvas.DisplayRT != null) return canvas.DisplayRT;

        if (whiteTex == null)
        {
            whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            whiteTex.name = "MapGlowWhite";
            whiteTex.hideFlags = HideFlags.HideAndDontSave;
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
        }
        return whiteTex;
    }

    static void Kill(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}
