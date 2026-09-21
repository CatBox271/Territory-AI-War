using Unity.Collections;
using UnityEngine;

/// <summary>
/// 子弹光效层：给 BulletManager 里的普通子弹（GPU 子弹，最多 65536 颗、不是 GameObject）加"发光点 + 多段残影拖尾"。
///
/// 为什么单独一层：
///   子弹是 NativeArray&lt;BulletData&gt; 里的数据、由 bulletDisplayCS 画进 BulletDisplayRT，没法给每颗挂 MapGlow 组件；
///   也不该去抢 MapGlowLayer 那 32 个炫光槽位（炮塔/大球的炫光在用）。
///   这里用 Graphics.DrawMeshInstanced 画实例化光点：每颗子弹 = 1 个头部光点（子弹的实际位置）+ 最近 K 帧的残影，
///   每 1023 个实例一批 → draw call 数量 = 发光的子弹数 / 1023（不是每颗一个）。
///
/// 参数都在 MapConfig 的"子弹炫光"分组里（开关/半径/强度/柔边/残影段数/残影衰减/发光上限）；
/// 颜色跟子弹显示同一个口径：MapConfig.GetColor(stage, ColorStage.Bullet)。
///
/// 自动创建：RuntimeInitializeOnLoadMethod 里挂在 BulletManager 那个物体下面；也可以自己在场景里放一个
/// （已有实例时不会重复创建）。层级参数（zOffset / renderQueue）在本组件上，见下方默认值。
/// </summary>
public class BulletGlowLayer : MonoBehaviour
{
    /// <summary>Graphics.DrawMeshInstanced 单批上限（Unity 硬限制 1023）。</summary>
    private const int MaxInstancesPerBatch = 1023;

    /// <summary>残影段数上限（环形缓冲按这个上限分配）。</summary>
    private const int MaxTrailSegments = 16;

    public static BulletGlowLayer Instance { get; private set; }

    [Header("层级（技术参数，一般不用动）")]
    [Tooltip("世界 z：地图 quad 在 0、子弹显示 quad 在 -0.05，这里取稍前一点")]
    public float zOffset = -0.055f;
    [Tooltip("渲染队列：2999 = 压在地图之上、子弹显示(3000)之下")]
    public int renderQueue = 2999;

    /// <summary>最近一帧实际提交的实例数（调参时可看）。</summary>
    public int LastDrawnInstances { get; private set; }
    /// <summary>最近一帧发光的子弹数（调参时可看）。</summary>
    public int LastGlowingBullets { get; private set; }

    private MapConfig config;
    private BulletManager manager;

    private Mesh quadMesh;
    private Material material;
    private MaterialPropertyBlock block;

    // 每颗子弹一份残影环形缓冲：trailPositions[i * segments + cursor]
    private Vector2[] trailPositions;
    private int[] trailCursor;
    private int[] trailFill;
    private int trailSegmentsAllocated;

    private readonly Matrix4x4[] batchMatrices = new Matrix4x4[MaxInstancesPerBatch];
    private readonly Vector4[] batchColors = new Vector4[MaxInstancesPerBatch];
    private int batchCount;

    private static readonly int InstanceColorId = Shader.PropertyToID("_InstanceColor");
    private static readonly int SoftnessId = Shader.PropertyToID("_Softness");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Ensure()
    {
        if (Instance != null) return;
        if (MapConfig.Instance == null) return;

        BulletManager m = BulletManager.Instance != null
            ? BulletManager.Instance
            : FindObjectOfType<BulletManager>();
        if (m == null) return;

        var go = new GameObject("BulletGlowLayer");
        go.transform.SetParent(m.transform, false);
        Instance = go.AddComponent<BulletGlowLayer>();
    }

    private void Awake()
    {
        Instance = this;
        config = MapConfig.Instance != null ? MapConfig.Instance : FindObjectOfType<MapConfig>();
        manager = BulletManager.Instance != null ? BulletManager.Instance : FindObjectOfType<BulletManager>();
        block = new MaterialPropertyBlock();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (quadMesh != null) Destroy(quadMesh);
        if (material != null) Destroy(material);
    }

    private void LateUpdate()
    {
        LastDrawnInstances = 0;
        LastGlowingBullets = 0;

        if (config == null) config = MapConfig.Instance;
        if (manager == null) manager = BulletManager.Instance;
        if (config == null || manager == null) return;
        if (!config.bulletGlowEnabled) return;

        NativeArray<BulletManager.BulletData> data = manager.Bullets;
        if (!data.IsCreated) return;

        float intensity = Mathf.Max(0f, config.bulletGlowIntensity);
        if (intensity <= 0f) return;

        // 改动参数要重烤一次材质；找不到材质就先不画
        if (!EnsureResources()) return;

        int segments = Mathf.Clamp(config.bulletGlowTrailSegments, 0, MaxTrailSegments);
        EnsureTrailBuffers(segments, data.Length);

        float size = Mathf.Max(0.0001f, config.bulletGlowRadius) * 2f;
        float fade = Mathf.Clamp01(config.bulletGlowTrailFade);

        int glowing = 0;
        batchCount = 0;

        for (int i = 0; i < data.Length; i++)
        {
            BulletManager.BulletData b = data[i];

            if (b.alive == 0)
            {
                // 子弹池会被复用：死了就清掉它的残影，免得下一发继承上一条尾巴
                if (trailFill != null && trailFill[i] != 0)
                {
                    trailFill[i] = 0;
                    trailCursor[i] = 0;
                }
                continue;
            }

            glowing++;

            Color team = config.GetColor(b.stage, MapConfig.ColorStage.Bullet);

            // ① 头部：子弹这一帧的实际位置
            AddInstance(new Vector3(b.position.x, b.position.y, zOffset), size, team, intensity);

            if (segments <= 1) continue;

            // ② 残影：先读环形缓冲里已有的位置（最新一条是上一帧），越老越暗
            int filled = trailFill[i];
            float bright = intensity;
            for (int age = 1; age <= filled; age++)
            {
                bright *= (1f - fade);
                if (bright <= 0.002f) break;

                int idx = trailCursor[i] - age;
                while (idx < 0) idx += segments;
                Vector2 p = trailPositions[i * segments + idx];
                AddInstance(new Vector3(p.x, p.y, zOffset), size, team, bright);
            }

            // ③ 再把这一帧的实际位置写进环形缓冲
            trailPositions[i * segments + trailCursor[i]] = b.position;
            trailCursor[i]++;
            if (trailCursor[i] >= segments) trailCursor[i] = 0;
            if (trailFill[i] < segments) trailFill[i]++;
        }

        Flush();
        LastGlowingBullets = glowing;
    }

    private void AddInstance(Vector3 position, float size, Color color, float brightness)
    {
        if (batchCount >= MaxInstancesPerBatch) Flush();

        batchMatrices[batchCount] = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(size, size, 1f));
        batchColors[batchCount] = new Vector4(
            color.r * brightness,
            color.g * brightness,
            color.b * brightness,
            1f);
        batchCount++;
    }

    private void Flush()
    {
        if (batchCount <= 0) return;

        block.SetVectorArray(InstanceColorId, batchColors);
        Graphics.DrawMeshInstanced(quadMesh, 0, material, batchMatrices, batchCount, block);
        LastDrawnInstances += batchCount;
        batchCount = 0;
    }

    private bool EnsureResources()
    {
        if (material == null)
        {
            Shader shader = Shader.Find("Custom/BulletGlow");
            if (shader == null)
            {
                Debug.LogError("[BulletGlow] 找不到 shader：Custom/BulletGlow（Assets/Shader/BulletGlow.shader 是否已导入？）");
                enabled = false;
                return false;
            }

            material = new Material(shader) { name = "BulletGlow(Runtime)" };
            material.enableInstancing = true;   // Graphics.DrawMeshInstanced 需要
            material.renderQueue = renderQueue;
        }

        if (quadMesh == null)
        {
            quadMesh = new Mesh { name = "BulletGlowQuad" };
            quadMesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f)
            };
            quadMesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };
            quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            quadMesh.RecalculateBounds();
        }

        if (config != null)
            material.SetFloat(SoftnessId, Mathf.Clamp(config.bulletGlowSoftness, 0.05f, 1f));

        return true;
    }

    private void EnsureTrailBuffers(int segments, int bulletCapacity)
    {
        if (segments <= 1)
        {
            if (trailSegmentsAllocated != 0)
            {
                trailPositions = null;
                trailCursor = null;
                trailFill = null;
                trailSegmentsAllocated = 0;
            }
            return;
        }

        if (trailSegmentsAllocated == segments && trailPositions != null && trailPositions.Length >= bulletCapacity * segments)
            return;

        trailPositions = new Vector2[bulletCapacity * segments];
        trailCursor = new int[bulletCapacity];
        trailFill = new int[bulletCapacity];
        trailSegmentsAllocated = segments;
    }
}
