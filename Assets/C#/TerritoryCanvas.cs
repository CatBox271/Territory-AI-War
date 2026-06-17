using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public class TerritoryCanvas : MonoBehaviour
{
    public ComputeShader initCompute;
    public ComputeShader paintCompute;
    public ComputeShader clearFlagCompute;
    public Material quadMaterial;

    private RenderTexture dataRT;
    private RenderTexture displayRT;
    private RenderTexture flagRT;
    private RenderTexture bulletDisplayRT;
    public RenderTexture DataRT => dataRT;
    public RenderTexture DisplayRT => displayRT;
    public RenderTexture BulletDisplayRT => bulletDisplayRT;
    private int paintKernel;
    private MapConfig config;

    public NativeArray<byte> territoryMap;

    void Awake()
    {
        config = MapConfig.Instance;
        dataRT = CreateRT();
        displayRT = CreateRT();
        flagRT = CreateRT(RenderTextureFormat.RFloat);
        bulletDisplayRT = CreateRT();
        territoryMap = new NativeArray<byte>(config.resolution * config.resolution, Allocator.Persistent);
        CreateMapQuad();
        CreateBulletQuad();
    }

    RenderTexture CreateRT(RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
    {
        var rt = new RenderTexture(config.resolution, config.resolution, 0, fmt);
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Point;
        rt.Create();
        return rt;
    }

    void CreateMapQuad()
    {
        var old = transform.Find("MapQuad");
        if (old) Destroy(old.gameObject);
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "MapQuad";
        q.transform.SetParent(transform);
        q.transform.localPosition = Vector3.zero;
        q.transform.localScale = new Vector3(config.worldSize, config.worldSize, 1f);
        q.GetComponent<MeshRenderer>().material = quadMaterial;
        quadMaterial.mainTexture = displayRT;
    }

    void CreateBulletQuad()
    {
        var old = transform.Find("BulletQuad");
        if (old) Destroy(old.gameObject);
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        q.name = "BulletQuad";
        q.transform.SetParent(transform);
        q.transform.localPosition = new Vector3(0, 0, -0.05f);
        q.transform.localScale = new Vector3(config.worldSize, config.worldSize, 1f);
        var mat = new Material(Shader.Find("Unlit/Transparent"));
        mat.mainTexture = bulletDisplayRT;
        mat.renderQueue = 3000;
        q.GetComponent<MeshRenderer>().material = mat;
    }

    void Start()
    {
        paintKernel = paintCompute.FindKernel("CSPaint");
        InitTerritory();
    }

    public void SetColorArrayPublic(ComputeShader cs) => SetColorArray(cs);

    void SetColorArray(ComputeShader cs)
    {
        int n = Mathf.Min(config.teamColors.Count, 17);
        var c = new Vector4[n];
        for (int i = 0; i < n; i++) c[i] = config.teamColors[i];
        cs.SetVectorArray("_TeamColors", c);
        cs.SetInt("_TeamCount", n);
    }

    void InitTerritory()
    {
        int k = initCompute.FindKernel("CSInit");
        initCompute.SetTexture(k, "DataResult", dataRT);
        initCompute.SetTexture(k, "DisplayResult", displayRT);
        initCompute.SetInt("_Resolution", config.resolution);
        SetColorArray(initCompute);
        initCompute.Dispatch(k, config.resolution / 8, config.resolution / 8, 1);
    }

    public int PaintSegment(Vector2 uvA, Vector2 uvB, int stage, float attackPower, float pixelRadius, int budget)
    {
        int ck = clearFlagCompute.FindKernel("CS_ClearFlag");
        clearFlagCompute.SetTexture(ck, "Flag", flagRT);
        clearFlagCompute.SetInt("_Resolution", config.resolution);
        clearFlagCompute.Dispatch(ck, config.resolution / 8, config.resolution / 8, 1);

        var counter = new ComputeBuffer(1, sizeof(uint));
        counter.SetData(new uint[] { 0 });

        paintCompute.SetTexture(paintKernel, "Flag", flagRT);
        paintCompute.SetTexture(paintKernel, "DataResult", dataRT);
        paintCompute.SetTexture(paintKernel, "DisplayResult", displayRT);
        paintCompute.SetBuffer(paintKernel, "ChangedCount", counter);
        paintCompute.SetVector("_PointA", uvA);
        paintCompute.SetVector("_PointB", uvB);
        paintCompute.SetFloat("_Radius", pixelRadius / config.resolution);
        paintCompute.SetInt("_Resolution", config.resolution);
        paintCompute.SetInt("_BallStage", stage);
        paintCompute.SetFloat("_AttackPower", attackPower);
        paintCompute.SetInt("_Budget", budget);
        SetColorArray(paintCompute);
        paintCompute.Dispatch(paintKernel, config.resolution / 8, config.resolution / 8, 1);
        counter.Release();

        // 同步更新 CPU 领地网格
        PaintToMap(uvA, uvB, stage, pixelRadius);

        // CPU 预估涂色消耗，避免 GPU 同步读回
        float uvDist = Vector2.Distance(uvA, uvB) * config.resolution;
        int estimated = Mathf.Min((int)(uvDist * pixelRadius * 2f) + 1, budget);
        return estimated;
    }

    void PaintToMap(Vector2 uvA, Vector2 uvB, int stage, float pixelRadius)
    {
        int res = config.resolution;
        int xA = Mathf.Clamp((int)(uvA.x * res + 0.5f), 0, res - 1);
        int yA = Mathf.Clamp((int)(uvA.y * res + 0.5f), 0, res - 1);
        int xB = Mathf.Clamp((int)(uvB.x * res + 0.5f), 0, res - 1);
        int yB = Mathf.Clamp((int)(uvB.y * res + 0.5f), 0, res - 1);
        int r = Mathf.CeilToInt(pixelRadius);
        int r2 = r * r;

        if (r <= 0 || stage < 0) return;

        int minX = Mathf.Max(0, Mathf.Min(xA, xB) - r);
        int maxX = Mathf.Min(res - 1, Mathf.Max(xA, xB) + r);
        int minY = Mathf.Max(0, Mathf.Min(yA, yB) - r);
        int maxY = Mathf.Min(res - 1, Mathf.Max(yA, yB) + r);

        int dx = xB - xA, dy = yB - yA;
        float lenSq = dx * dx + dy * dy;
        byte bStage = (byte)stage;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float distSq;
                if (lenSq < 0.5f)
                    distSq = (x - xA) * (x - xA) + (y - yA) * (y - yA);
                else
                {
                    float t = Mathf.Clamp01(((x - xA) * dx + (y - yA) * dy) / lenSq);
                    float px = xA + t * dx, py = yA + t * dy;
                    distSq = (x - px) * (x - px) + (y - py) * (y - py);
                }
                if (distSq <= r2)
                    territoryMap[y * res + x] = bStage;
            }
        }
    }

    public void MarkBulletTrail(float2 worldA, float2 worldB, int stage)
    {
        int res = config.resolution;
        float ms = config.worldSize;
        int x0 = (int)((worldA.x + ms * 0.5f) / ms * res + 0.5f);
        int y0 = (int)((worldA.y + ms * 0.5f) / ms * res + 0.5f);
        int x1 = (int)((worldB.x + ms * 0.5f) / ms * res + 0.5f);
        int y1 = (int)((worldB.y + ms * 0.5f) / ms * res + 0.5f);
        x0 = Mathf.Clamp(x0, 0, res - 1); y0 = Mathf.Clamp(y0, 0, res - 1);
        x1 = Mathf.Clamp(x1, 0, res - 1); y1 = Mathf.Clamp(y1, 0, res - 1);

        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        byte bStage = (byte)stage;

        while (true)
        {
            territoryMap[y0 * res + x0] = bStage;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    public int EstimatePaintCost(Vector2 uvA, Vector2 uvB, int stage, float pixelRadius)
    {
        int res = config.resolution;
        int xA = Mathf.Clamp((int)(uvA.x * res + 0.5f), 0, res - 1);
        int yA = Mathf.Clamp((int)(uvA.y * res + 0.5f), 0, res - 1);
        int xB = Mathf.Clamp((int)(uvB.x * res + 0.5f), 0, res - 1);
        int yB = Mathf.Clamp((int)(uvB.y * res + 0.5f), 0, res - 1);
        int r = Mathf.CeilToInt(pixelRadius);
        int r2 = r * r;
        if (r <= 0 || stage < 0) return 0;

        int minX = Mathf.Max(0, Mathf.Min(xA, xB) - r);
        int maxX = Mathf.Min(res - 1, Mathf.Max(xA, xB) + r);
        int minY = Mathf.Max(0, Mathf.Min(yA, yB) - r);
        int maxY = Mathf.Min(res - 1, Mathf.Max(yA, yB) + r);

        int dx = xB - xA, dy = yB - yA;
        float lenSq = dx * dx + dy * dy;
        byte bStage = (byte)stage;
        int cost = 0;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float distSq;
                if (lenSq < 0.5f)
                    distSq = (x - xA) * (x - xA) + (y - yA) * (y - yA);
                else
                {
                    float t = Mathf.Clamp01(((x - xA) * dx + (y - yA) * dy) / lenSq);
                    float px = xA + t * dx, py = yA + t * dy;
                    distSq = (x - px) * (x - px) + (y - py) * (y - py);
                }
                if (distSq <= r2 && territoryMap[y * res + x] != bStage)
                    cost++;
            }
        }
        return cost;
    }

    void OnDestroy()
    {
        if (dataRT) dataRT.Release();
        if (displayRT) displayRT.Release();
        if (flagRT) flagRT.Release();
        if (bulletDisplayRT) bulletDisplayRT.Release();
        if (territoryMap.IsCreated) territoryMap.Dispose();
    }
}
