using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class BulletManager : MonoBehaviour
{
    public static BulletManager Instance { get; private set; }

    public ComputeShader bulletPaintCS;
    public ComputeShader bulletDisplayCS;
    [Range(1, 8)] public int bulletPaintRadius = 3;
    [Range(1, 8)] public int bulletDisplayRadius = 2;
    [Range(1, 50)] public float bulletSpeed = 10f;

    private const int MAX_BULLETS = 8192;
    private const int MAX_COLLIDERS = 128;
    private const int HITS_CAPACITY = 4096;
    private const int GATHER_INTERVAL = 30;

    private NativeArray<BulletData> bullets;
    private BulletData[] bulletArray;
    private ComputeBuffer bulletBuffer;
    private int activeCount;
    public int ActiveCount => activeCount;
    public static int MaxBullets => MAX_BULLETS;

    private NativeArray<BallCollider> ballColliders;
    private NativeArray<ShieldCollider> shieldColliders;
    private BallPainter[] cachedBalls;
    private Towel[] cachedTowels;
    private int ballCount, shieldCount;
    private NativeList<BulletHit> hits;
    private int[] bulletGrid;
    private const int GRID_RES = 400;

    private Vector4[] cachedTeamColors;
    private Vector4[] cachedBulletColors;
    private int cachedColorCount;
    private TerritoryCanvas canvas;
    private int gatherTimer;
    private bool initialized;
    private float displayLinger;
    private bool needDisplayClear;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        bullets = new NativeArray<BulletData>(MAX_BULLETS, Allocator.Persistent);
        bulletArray = new BulletData[MAX_BULLETS];
        bulletBuffer = new ComputeBuffer(MAX_BULLETS, BulletData.GPUSize);
        ballColliders = new NativeArray<BallCollider>(MAX_COLLIDERS, Allocator.Persistent);
        shieldColliders = new NativeArray<ShieldCollider>(MAX_COLLIDERS, Allocator.Persistent);
        cachedBalls = new BallPainter[MAX_COLLIDERS];
        cachedTowels = new Towel[MAX_COLLIDERS];
        hits = new NativeList<BulletHit>(HITS_CAPACITY, Allocator.Persistent);
        bulletGrid = new int[GRID_RES * GRID_RES];
        cachedTeamColors = new Vector4[17];
        cachedBulletColors = new Vector4[17];
        canvas = FindObjectOfType<TerritoryCanvas>();
        gatherTimer = GATHER_INTERVAL;
    }

    void Start()
    {
        if (canvas == null) canvas = FindObjectOfType<TerritoryCanvas>();
        initialized = canvas != null && bulletPaintCS != null && bulletDisplayCS != null;
        RefreshColorCache();
    }

    void RefreshColorCache()
    {
        var tc = MapConfig.Instance.teamColors;
        cachedColorCount = Mathf.Min(tc.Count, 17);
        for (int i = 0; i < cachedColorCount; i++)
        {
            cachedTeamColors[i] = new Vector4(tc[i].r, tc[i].g, tc[i].b, tc[i].a);
            Color bc = MapConfig.Instance.GetColor(i, MapConfig.ColorStage.Bullet);
            cachedBulletColors[i] = new Vector4(bc.r, bc.g, bc.b, 1f);
        }
    }
    public void Fire(Vector2 worldPos, Vector2 direction, int stage, int value, float speed = -1f)
    => Fire(new float2(worldPos.x, worldPos.y), new float2(direction.x, direction.y), stage, value, speed);
    public void Fire(Vector3 worldPos, Vector3 direction, int stage, int value, float speed = -1f)
        => Fire(new float2(worldPos.x, worldPos.y), new float2(direction.x, direction.y), stage, value, speed);

    public void Fire(float2 worldPos, float2 direction, int stage, int value, float speed = -1f)
    {
        if (value <= 0) return;
        float spd = speed > 0 ? speed : bulletSpeed;

        for (int i = 0; i < MAX_BULLETS; i++)
        {
            var b = bullets[i];
            if (b.alive == 0)
            {
                b.position = worldPos;
                b.oldPosition = worldPos;
                b.velocity = math.normalizesafe(direction) * spd;
                b.stage = stage;
                b.alive = 1;
                b.attackPower = 1f;
                b.value = value;
                bullets[i] = b;
                activeCount++;
                return;
            }
        }

        Debug.LogWarning($"BulletManager: 满! {activeCount}/{MAX_BULLETS}");
    }

    public void DebugStats()
    {
        int alive = 0;
        var perTeam = new int[17][];
        for (int t = 0; t < 17; t++) perTeam[t] = new int[3]; // [alive, totalFired, valueConsumed]

        for (int i = 0; i < MAX_BULLETS; i++)
        {
            var b = bullets[i];
            if (b.alive != 0)
            {
                alive++;
                int s = math.clamp(b.stage, 0, 16);
                perTeam[s][0]++;
                perTeam[s][1]++;
            }
        }
        for (int i = 0; i < hits.Length; i++)
        {
            int s = math.clamp(bullets[hits[i].bulletIndex].stage, 0, 16);
            perTeam[s][2] += hits[i].value;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"子弹: {alive}/{MAX_BULLETS}\n队伍: 存活 碰撞消耗\n");
        for (int t = 1; t <= 8; t++)
        {
            if (perTeam[t][0] > 0 || perTeam[t][2] > 0)
                sb.Append($"  [{t}] {perTeam[t][0]}发 {perTeam[t][2]}消耗\n");
        }
        Debug.Log(sb.ToString());
    }

    void Update()
    {
        if (!initialized) return;

        // 按 F3 输出各队伍统计
        if (Input.GetKeyDown(KeyCode.F3)) DebugStats();

        if (activeCount <= 0)
        {
            if (++gatherTimer >= GATHER_INTERVAL) { gatherTimer = 0; RefreshColliders(); }
            return;
        }

        gatherTimer++;
        float dt = Time.deltaTime;
        var config = MapConfig.Instance;

        if (gatherTimer >= GATHER_INTERVAL) { gatherTimer = 0; RefreshColliders(); }

        var moveJob = new MoveBulletsJob
        {
            bullets = bullets,
            dt = dt,
            mapHalfSize = config.worldSize * 0.5f
        };
        JobHandle moveHandle = moveJob.Schedule(MAX_BULLETS, 64);
        moveHandle.Complete();

        // 主线程子弹间碰撞检测（空间网格，沿路径自适应采样并占位整条路径）
        System.Array.Fill(bulletGrid, -1);
        float halfWS = config.worldSize * 0.5f;
        float ws = config.worldSize;
        float cellSize = ws / GRID_RES;
        for (int bi = 0; bi < MAX_BULLETS; bi++)
        {
            var b = bullets[bi];
            if (b.alive == 0) continue;

            float pathLen = math.distance(b.position, b.oldPosition);
            int samples = math.clamp((int)(pathLen / cellSize) + 2, 2, 8);
            int bestGx = 0, bestGy = 0;
            bool killed = false;

            // 检查所有采样格是否有异队子弹
            for (int s = 0; s < samples && !killed; s++)
            {
                float t = s / (float)(samples - 1);
                int gx = (int)(((b.oldPosition.x + (b.position.x - b.oldPosition.x) * t) + halfWS) / ws * GRID_RES);
                int gy = (int)(((b.oldPosition.y + (b.position.y - b.oldPosition.y) * t) + halfWS) / ws * GRID_RES);
                gx = math.clamp(gx, 0, GRID_RES - 1);
                gy = math.clamp(gy, 0, GRID_RES - 1);
                if (s == samples - 1) { bestGx = gx; bestGy = gy; }

                for (int d = -1; d <= 1 && !killed; d++)
                {
                    int nx = gx + d, ny = gy;
                    if ((uint)nx < GRID_RES)
                    {
                        int prev = bulletGrid[ny * GRID_RES + nx];
                        if (prev != -1 && prev != bi)
                        {
                            var o = bullets[prev];
                            if (o.alive != 0 && o.stage != b.stage)
                            { b.alive = 0; b.value = 0; o.alive = 0; o.value = 0; bullets[prev] = o; killed = true; break; }
                        }
                    }
                    nx = gx; ny = gy + d;
                    if ((uint)ny < GRID_RES)
                    {
                        int prev = bulletGrid[ny * GRID_RES + nx];
                        if (prev != -1 && prev != bi)
                        {
                            var o = bullets[prev];
                            if (o.alive != 0 && o.stage != b.stage)
                            { b.alive = 0; b.value = 0; o.alive = 0; o.value = 0; bullets[prev] = o; killed = true; break; }
                        }
                    }
                }
            }

            // 未碰撞 → 占住路径上所有采样格的十字邻居
            if (!killed)
            {
                for (int s = 0; s < samples; s++)
                {
                    float t = s / (float)(samples - 1);
                    int gx = (int)(((b.oldPosition.x + (b.position.x - b.oldPosition.x) * t) + halfWS) / ws * GRID_RES);
                    int gy = (int)(((b.oldPosition.y + (b.position.y - b.oldPosition.y) * t) + halfWS) / ws * GRID_RES);
                    gx = math.clamp(gx, 0, GRID_RES - 1);
                    gy = math.clamp(gy, 0, GRID_RES - 1);

                    for (int d = -1; d <= 1; d++)
                    {
                        int nx = gx + d, ny = gy;
                        if ((uint)nx < GRID_RES) bulletGrid[ny * GRID_RES + nx] = bi;
                        nx = gx; ny = gy + d;
                        if ((uint)ny < GRID_RES) bulletGrid[ny * GRID_RES + nx] = bi;
                    }
                }
            }

            bullets[bi] = b;
        }

        hits.Clear();
        if (ballCount > 0)
        {
            var ballJob = new BulletBallCollisionJob
            {
                bullets = bullets,
                balls = ballColliders,
                ballCount = ballCount,
                hitWriter = hits.AsParallelWriter()
            };
            JobHandle ballHandle = ballJob.Schedule(MAX_BULLETS, 64);
            if (shieldCount > 0)
            {
                var shJob = new BulletShieldCollisionJob
                {
                    bullets = bullets, shields = shieldColliders,
                    shieldCount = shieldCount, hitWriter = hits.AsParallelWriter()
                };
                shJob.Schedule(MAX_BULLETS, 64, ballHandle).Complete();
            }
            else ballHandle.Complete();
        }
        else if (shieldCount > 0)
        {
            new BulletShieldCollisionJob
            {
                bullets = bullets, shields = shieldColliders,
                shieldCount = shieldCount, hitWriter = hits.AsParallelWriter()
            }.Schedule(MAX_BULLETS, 64).Complete();
        }

        int alive = CountAndApplyHits();

        // 串行绘制子弹轨迹到领地网格——先画的抢占领地，后画的（重叠散弹）免消耗
        var map = canvas.territoryMap;
        int res = config.resolution;
        float ms = config.worldSize;
        float half = ms * 0.5f;
        for (int i = 0; i < MAX_BULLETS; i++)
        {
            var b = bullets[i];
            if (b.alive == 0 || b.value <= 0) continue;

            int x0 = (int)((b.oldPosition.x + half) / ms * res + 0.5f);
            int y0 = (int)((b.oldPosition.y + half) / ms * res + 0.5f);
            int x1 = (int)((b.position.x + half) / ms * res + 0.5f);
            int y1 = (int)((b.position.y + half) / ms * res + 0.5f);
            x0 = Mathf.Clamp(x0, 0, res - 1); y0 = Mathf.Clamp(y0, 0, res - 1);
            x1 = Mathf.Clamp(x1, 0, res - 1); y1 = Mathf.Clamp(y1, 0, res - 1);

            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            byte bStage = (byte)b.stage;
            int r = bulletPaintRadius;
            int cost = 0;

            while (true)
            {
                for (int dy2 = -r; dy2 <= r; dy2++)
                {
                    int py = y0 + dy2;
                    if ((uint)py >= res) continue;
                    for (int dx2 = -r; dx2 <= r; dx2++)
                    {
                        int px = x0 + dx2;
                        if ((uint)px >= res) continue;
                        byte pixelOwner = map[py * res + px];
                        if (pixelOwner != bStage)
                        {
                            map[py * res + px] = bStage;
                            cost += pixelOwner == 0 ? 1 : 2; // 中立 1x，敌队 2x
                        }
                    }
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }

            if (cost > 0)
            {
                if (b.value > cost) b.value -= cost;
                else { b.value = 0; b.alive = 0; }
                bullets[i] = b;
            }
        }

        if (alive > 0)
        {
            displayLinger = 3f;
            needDisplayClear = true;
        }
        else
        {
            activeCount = 0;
            displayLinger -= Time.deltaTime;
        }

        // 始终上传+渲染——活着正常画，linger 期间画零化数据，过期后清一次
        if (alive > 0 || displayLinger > 0f)
        {
            RenderBulletPaint(alive);
            RenderBulletDisplay();
        }
        else if (needDisplayClear)
        {
            ClearBulletDisplay();
            needDisplayClear = false;
        }
    }

    void RefreshColliders()
    {
        ballCount = 0;
        foreach (var bp in FindObjectsOfType<BallPainter>())
        {
            if (ballCount >= MAX_COLLIDERS) break;
            if (bp == null || bp.value <= 0) continue;
            float2 p = new float2(bp.transform.position.x, bp.transform.position.y);
            float r = Mathf.Max(bp.transform.lossyScale.x, bp.transform.lossyScale.y) * bp.baseWorldRadius;
            int v = bp.value > int.MaxValue ? int.MaxValue : (int)((HugeInt)bp.value).ToLong();
            ballColliders[ballCount] = new BallCollider { position = p, radius = r, stage = bp.stage, value = v };
            cachedBalls[ballCount] = bp;
            ballCount++;
        }

        shieldCount = 0;
        foreach (var t in FindObjectsOfType<Towel>())
        {
            if (shieldCount >= MAX_COLLIDERS) break;
            if (t == null || t.shield == null || t.shield_value <= 0) continue;
            float2 p = new float2(t.transform.position.x, t.transform.position.y);
            float r = t.shield.transform.lossyScale.x;
            int v = t.shield_value > int.MaxValue ? int.MaxValue : (int)((HugeInt)t.shield_value).ToLong();
            shieldColliders[shieldCount] = new ShieldCollider { position = p, radius = r, stage = t.stage, value = v };
            cachedTowels[shieldCount] = t;
            shieldCount++;
        }
    }

    int CountAndApplyHits()
    {
        int alive = 0;
        for (int i = 0; i < MAX_BULLETS; i++)
            if (bullets[i].alive != 0) alive++;

        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i];
            if (hit.targetType == 0 && hit.targetIndex < MAX_COLLIDERS)
            {
                var bp = cachedBalls[hit.targetIndex];
                if (bp == null) continue;

                float mass = hit.value / 81920000f;
                var impulse = new Vector2(hit.bulletVelocity.x, hit.bulletVelocity.y) * mass * MapConfig.Instance.BulletImpactForce;
                if (bp.rb != null) bp.rb.AddForce(impulse, ForceMode2D.Impulse);

                if (hit.sameTeam) ((IStageValue)bp).Heal(bullets[hit.bulletIndex].stage, new HugeInt(hit.value));
                else bp.Spend(hit.value);
            }
            else if (hit.targetType == 1 && hit.targetIndex < MAX_COLLIDERS)
            {
                var t = cachedTowels[hit.targetIndex];
                if (t == null || t.shieldSV == null) continue;
                if (!hit.sameTeam)
                {
                    var cost = t.shieldSV.Hit(bullets[hit.bulletIndex].stage, new HugeInt(hit.value));
                    var bi = bullets[hit.bulletIndex];
                    bi.value -= (int)((HugeInt)cost).ToLong();
                    if (bi.value <= 0) { bi.alive = 0; bi.value = 0; }
                    bullets[hit.bulletIndex] = bi;
                }
            }
        }

        activeCount = alive;
        return alive;
    }

    void RenderBulletPaint(int aliveCount)
    {
        if (bulletPaintCS == null || canvas == null) return;
        var config = MapConfig.Instance;

        for (int i = 0; i < MAX_BULLETS; i++)
        {
            var bd = bullets[i];
            if (bd.alive == 0) { bd.position = float2.zero; bd.oldPosition = float2.zero; }
            bulletArray[i] = bd;
        }
        bulletBuffer.SetData(bulletArray);

        int k = bulletPaintCS.FindKernel("CSBulletPaint");
        bulletPaintCS.SetBuffer(k, "Bullets", bulletBuffer);
        bulletPaintCS.SetTexture(k, "DataResult", canvas.DataRT);
        bulletPaintCS.SetTexture(k, "DisplayResult", canvas.DisplayRT);
        bulletPaintCS.SetInt("_Resolution", config.resolution);
        bulletPaintCS.SetFloat("_MapHalfSize", config.worldSize * 0.5f);
        bulletPaintCS.SetFloat("_PixelToWorld", config.worldSize / (float)config.resolution);
        bulletPaintCS.SetInt("_BulletRadius", bulletPaintRadius);
        SetTeamColors(bulletPaintCS);
        bulletPaintCS.Dispatch(k, Mathf.CeilToInt(MAX_BULLETS / 64f), 1, 1);
        // CPU 端领地网格已处理 paint 耗值，无需 GPU 回读
    }

    void RenderBulletDisplay()
    {
        if (bulletDisplayCS == null || canvas == null) return;
        var config = MapConfig.Instance;

        int ck = bulletDisplayCS.FindKernel("CSClearDisplay");
        bulletDisplayCS.SetTexture(ck, "Display", canvas.BulletDisplayRT);
        bulletDisplayCS.Dispatch(ck, config.resolution / 8, config.resolution / 8, 1);

        int k = bulletDisplayCS.FindKernel("CSBulletDisplay");
        bulletDisplayCS.SetBuffer(k, "Bullets", bulletBuffer);
        bulletDisplayCS.SetTexture(k, "Display", canvas.BulletDisplayRT);
        bulletDisplayCS.SetInt("_Resolution", config.resolution);
        bulletDisplayCS.SetFloat("_MapHalfSize", config.worldSize * 0.5f);
        bulletDisplayCS.SetFloat("_PixelToWorld", config.worldSize / (float)config.resolution);
        bulletDisplayCS.SetInt("_BulletRadius", bulletDisplayRadius);
        SetBulletColors(bulletDisplayCS);
        bulletDisplayCS.Dispatch(k, Mathf.CeilToInt(MAX_BULLETS / 64f), 1, 1);
    }

    void ClearBulletDisplay()
    {
        if (bulletDisplayCS == null || canvas == null) return;
        int ck = bulletDisplayCS.FindKernel("CSClearDisplay");
        bulletDisplayCS.SetTexture(ck, "Display", canvas.BulletDisplayRT);
        bulletDisplayCS.Dispatch(ck, MapConfig.Instance.resolution / 8, MapConfig.Instance.resolution / 8, 1);
    }

    void SetTeamColors(ComputeShader cs) { cs.SetVectorArray("_TeamColors", cachedTeamColors); cs.SetInt("_TeamCount", cachedColorCount); }
    void SetBulletColors(ComputeShader cs) { cs.SetVectorArray("_BulletColors", cachedBulletColors); cs.SetInt("_TeamCount", cachedColorCount); }

    void OnDestroy()
    {
        if (bullets.IsCreated) bullets.Dispose();
        if (ballColliders.IsCreated) ballColliders.Dispose();
        if (shieldColliders.IsCreated) shieldColliders.Dispose();
        if (hits.IsCreated) hits.Dispose();
        bulletBuffer?.Release();
    }

    public struct BulletData
    {
        public float2 position;
        public float2 oldPosition;
        public float2 velocity;
        public int stage;
        public int alive;
        public float attackPower;
        public int value;
        public static int GPUSize => 40;
    }

    public struct BallCollider
    {
        public float2 position;
        public float radius;
        public int stage;
        public int value;
    }

    public struct ShieldCollider
    {
        public float2 position;
        public float radius;
        public int stage;
        public int value;
    }

    public struct BulletHit
    {
        public int targetType;
        public int targetIndex;
        public int bulletIndex;
        public bool sameTeam;
        public int value;
        public float attackPower;
        public float2 bulletVelocity;
    }
}
