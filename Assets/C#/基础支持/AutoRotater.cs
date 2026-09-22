using UnityEngine;
using UnityEngine.Rendering;

// 自动旋转器：无锁定目标时按默认模式旋转；
// 开启自动护卫后，会优先在 guardRadius 内寻找最近的敌方领土并持续朝向它。
public class AutoRotater : MonoBehaviour
{
    [Header("默认旋转")]
    public float speed = 30f;
    public float guard_speed = 1800f;
    public Vector2 angleRange = new Vector2(0f, 360f);
    public bool loop = true;
    public bool active = true;

    [Header("自动护卫")]
    public bool guardMode = true;
    [Tooltip("护卫检测半径（世界单位），默认 4")]
    public float guardRadius = 4f;
    public float guardUpdateInterval = 0.2f;
    public ComputeShader guardCompute; // 不填时自动退回 CPU 扫描

    // 同一个场景里只要有任意一个炮塔在 Inspector 里填了 shader，其余实例（例如从预制体解包出来、
    // 忘记重新指定引用的场景实例）会自动复用同一个资产，避免悄悄退回 CPU 逐像素扫描。
    private static ComputeShader sharedGuardCompute;
    private static bool sharedGuardComputeSearched;
    private bool guardGpuFallbackWarned;

    private float currentAngle;
    private int direction = 1;

    private Towel owner;
    private MapConfig config;
    private Vector2 guardTarget;
    private bool hasGuardTarget;
    private float nextGuardScanTime;

    private Vector2 lastGuardTarget;
    private bool hasLastGuardTarget;
    private bool guardTurning;

    private ComputeBuffer guardResultBuffer;
    private int guardKernel = -1;
    private uint[] guardResultData;
    private AsyncGPUReadbackRequest guardReadback;
    private bool guardReadbackPending;
    private float guardReadbackStartTime;
    private bool guardScanPending;
    private bool guardGpuLogged;

    void Start()
    {
        RandomWay("start");
        currentAngle = NormalizeAngle(transform.localEulerAngles.z);
        owner = GetComponentInParent<Towel>();
        config = MapConfig.Instance;

        if (owner != null && ResolveGuardCompute())
        {
            guardKernel = guardCompute.FindKernel("CSFindNearest");
            guardResultBuffer = new ComputeBuffer(1, sizeof(uint));
            guardResultData = new uint[1];
        }
    }

    /// <summary>
    /// 解析护卫扫描用的 compute shader：Inspector 引用 → 同场景其它实例共享的引用 → Resources/TerritoryNearest。
    /// 这样即使某个实例（比如从预制体解包出来的场景炮塔）忘了填引用，也不会悄悄退回 CPU 全图逐像素扫描。
    /// </summary>
    bool ResolveGuardCompute()
    {
        if (guardCompute == null) guardCompute = sharedGuardCompute;
        if (guardCompute == null && !sharedGuardComputeSearched)
        {
            sharedGuardComputeSearched = true;
            guardCompute = Resources.Load<ComputeShader>("TerritoryNearest");
        }
        if (guardCompute != null) sharedGuardCompute = guardCompute;
        return guardCompute != null;
    }

    void OnDestroy()
    {
        if (guardResultBuffer != null)
        {
            // 回读还在飞就先等它落地，否则释放 buffer 会报“还在使用中”
            if (guardReadbackPending)
            {
                guardReadback.WaitForCompletion();
                guardReadbackPending = false;
            }
            guardResultBuffer.Release();
            guardResultBuffer = null;
        }
    }

    /// <summary>运行时修改护卫检测半径（世界单位）。</summary>
    public void SetGuardRadius(float radius)
    {
        guardRadius = Mathf.Max(0f, radius);
        hasGuardTarget = false; // 下次更新立即按新半径重新扫描
    }

    // ==================== 禁射扇区（结盟时避开某一段角度） ====================
    // 待机旋转与自动护卫都**不朝扇区里的方向**：待机旋转转到边界就掉头，
    // 自动护卫遇到"目标在扇区里 / 转过去要横穿扇区"就放弃这次护卫转向。
    // 手动接管（AimController 持续瞄准）不受它限制 —— 那是明确要打这个方向。

    /// <summary>禁射扇区中心（**内部角**口径 = currentAngle，0 = 炮口朝上）。</summary>
    public float avoidCenter;
    /// <summary>禁射扇区半宽（度）。&lt;= 0 = 没设。</summary>
    public float avoidHalf;
    /// <summary>是否设了禁射扇区。</summary>
    public bool AvoidEnabled => avoidHalf > 0f;
    /// <summary>禁射扇区中心的世界方向角（度）：0 = 地图右(+X)，逆时针为正。</summary>
    public float AvoidCenterWorld => NormalizeAngle(avoidCenter + 90f);

    /// <summary>
    /// 设置禁射扇区。centerMath = 世界方向角（度，0 = 地图右(+X)、逆时针为正，和 move_turret 的 angle 同一口径）；
    /// half = 半宽（度，0 = 取消）。只记一个扇区，再设一次就覆盖。
    /// </summary>
    public void SetAvoidSector(float centerMath, float half)
    {
        if (half <= 0f) { ClearAvoidSector(); return; }

        avoidCenter = NormalizeAngle(centerMath - 90f);   // 世界角 -> 内部角（炮口方向是 transform.up）
        avoidHalf = Mathf.Clamp(half, 0f, 179f);
        if (IsAvoided(currentAngle)) currentAngle = CloserAvoidEdge(currentAngle);   // 别停在禁射方向
    }

    /// <summary>取消禁射扇区（恢复整圈都能转）。</summary>
    public void ClearAvoidSector()
    {
        avoidHalf = 0f;
    }

    /// <summary>这个内部角（currentAngle 口径）是不是落在禁射扇区里。</summary>
    public bool IsAvoided(float angle)
    {
        if (!AvoidEnabled) return false;
        return Mathf.Abs(Mathf.DeltaAngle(angle, avoidCenter)) <= avoidHalf;
    }

    /// <summary>禁射扇区的文字描述（世界口径）；没设返回 null。情报与工具回执共用。</summary>
    public string DescribeAvoid()
    {
        if (!AvoidEnabled) return null;
        float rad = AvoidCenterWorld * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        return $"中心 {AvoidCenterWorld:0}°（{InformGetter.CardinalDirection(dir)}）±{avoidHalf:0}°";
    }

    /// <summary>离这个内部角最近的那个扇区边界。</summary>
    float CloserAvoidEdge(float angle)
    {
        float a = NormalizeAngle(avoidCenter - avoidHalf);
        float b = NormalizeAngle(avoidCenter + avoidHalf);
        return Mathf.Abs(Mathf.DeltaAngle(angle, a)) <= Mathf.Abs(Mathf.DeltaAngle(angle, b)) ? a : b;
    }

    /// <summary>从 from 转到 to（内部角）走最短路径，中途会不会横穿禁射扇区。</summary>
    bool PathCrossesAvoid(float from, float to)
    {
        if (!AvoidEnabled) return false;
        if (IsAvoided(to)) return true;

        const int steps = 32;
        float delta = Mathf.DeltaAngle(from, to);
        for (int i = 1; i <= steps; i++)
            if (IsAvoided(NormalizeAngle(from + delta * i / steps))) return true;
        return false;
    }

    /// <summary>护卫目标是不是被禁射扇区挡着（目标就在扇区里，或转过去要横穿扇区）。</summary>
    bool GuardTargetAvoided(Vector2 target)
    {
        if (!AvoidEnabled) return false;
        Vector2 dir = target - (Vector2)transform.position;
        if (dir.sqrMagnitude < 1e-8f) return false;

        float to = NormalizeAngle(currentAngle + Vector2.SignedAngle(transform.up, dir));
        return PathCrossesAvoid(currentAngle, to);
    }

    private string last_state;
    private void RandomWay(string state)
    {
        if (last_state != state)
        {
            direction = Random.Range(0, 2) == 0 ? -1 : 1;
            last_state = state;
        }
    }

    void Update()
    {
        if (!active) return;

        bool rotated;
        if (guardMode && TryGetGuardTarget(out Vector2 target) && !GuardTargetAvoided(target))
        {
            // 新目标：先用 guard_speed 快速面对它
            if (!hasLastGuardTarget || target != lastGuardTarget)
            {
                lastGuardTarget = target;
                hasLastGuardTarget = true;
                guardTurning = true;
            }

            // 已经面对目标后，子弹飞行期间它还没被涂抹：
            // 切回默认模式，以 speed 恒定速度转，而不是继续瞄着目标。
            if (guardTurning && IsFacingTarget(target))
            {
                guardTurning = false;
            }

            if (guardTurning)
            {
                RandomWay("guard_turn");
                RotateTo(target, guard_speed);
            }
            else
            {
                RandomWay("guard_auto");
                rotated = DefaultRotate();
                if (!rotated) return;
            }
            rotated = true;
        }
        else
        {
            RandomWay("auto");
            hasLastGuardTarget = false;
            guardTurning = false;
            rotated = DefaultRotate();
        }

        if (!rotated) return;

        Vector3 euler = transform.localEulerAngles;
        euler.z = currentAngle;
        transform.localEulerAngles = euler;
    }

    bool DefaultRotate()
    {
        float min = angleRange.x;
        float max = angleRange.y;

        float prev = currentAngle;
        currentAngle += speed * Time.deltaTime * direction;

        if (loop)
        {
            float range = max - min;
            if (range <= 0f) return false;
            while (currentAngle > max) currentAngle -= range;
            while (currentAngle < min) currentAngle += range;
        }
        else
        {
            if (currentAngle >= max)
            {
                currentAngle = max;
                direction = -1;
            }
            else if (currentAngle <= min)
            {
                currentAngle = min;
                direction = 1;
            }
        }

        // 禁射扇区：不进去，贴着刚越过的那个边界掉头
        if (IsAvoided(currentAngle))
        {
            direction = -direction;
            currentAngle = CloserAvoidEdge(prev);
        }
        return true;
    }

    bool TryGetGuardTarget(out Vector2 target)
    {
        target = default;
        if (owner == null || config == null) return false;
        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return false;

        // 按间隔重新扫描，避免每帧全图查询；两次扫描之间沿用上一次的目标
        if (Time.time < nextGuardScanTime)
        {
            if (!hasGuardTarget) return false;
            target = guardTarget;
            return true;
        }
        nextGuardScanTime = Time.time + guardUpdateInterval;

        // 回读卡住超过 0.5 秒就当这次丢了，下次重新发，别永远停在旧目标上
        if (guardReadbackPending && Time.realtimeSinceStartup - guardReadbackStartTime > 0.5f)
            guardReadbackPending = false;

        if (ScanNearestEnemyTerritory(out Vector2 scanned))
        {
            guardTarget = scanned;
            hasGuardTarget = true;
        }
        else if (!guardScanPending)
        {
            // 结果已经确定：半径内确实没有敌方领土
            hasGuardTarget = false;
        }
        // guardScanPending：异步结果还在路上，先保持上一次的目标

        if (!hasGuardTarget) return false;
        target = guardTarget;
        return true;
    }

    bool ScanNearestEnemyTerritory(out Vector2 target)
    {
        guardScanPending = false;

        // 优先走 GPU；没配置 shader / 纹理不可用 / 半径超编码范围时，才退回 CPU 逐个像素扫
        if (CanUseGuardGPU(out string gpuWhyNot)) return ScanNearestTerritoryGPU(out target);

        if (!guardGpuFallbackWarned)
        {
            guardGpuFallbackWarned = true;
            Debug.LogWarning($"AutoRotater({name}) 护卫最近点扫描退回 CPU：{gpuWhyNot}", this);
        }
        return ScanNearestTerritoryCPU(out target);
    }

    /// <summary>
    /// GPU 路径是否可用。结果编码是「12 位距离 + 10 位 x + 10 位 y」，
    /// 因此分辨率上限 1024、半径上限 511 像素；超了宁可退回 CPU，也不能静默算错坐标。
    /// </summary>
    bool CanUseGuardGPU(out string whyNot)
    {
        whyNot = null;

        if (guardCompute == null)
        {
            whyNot = "没有 compute shader（Inspector 里指定 TerritoryNearest.compute，或把它放进 Resources 目录）";
            return false;
        }
        if (guardKernel < 0 || guardResultBuffer == null)
        {
            whyNot = "compute shader 初始化失败";
            return false;
        }

        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || canvas.DataRT == null)
        {
            whyNot = "领地画布（TerritoryCanvas.DataRT）不可用";
            return false;
        }
        if (config == null || config.worldSize <= 0f)
        {
            whyNot = "MapConfig 不可用";
            return false;
        }

        int res = config.resolution;
        if (res <= 0 || res > 1024)
        {
            whyNot = $"resolution={res} 超出 GPU 结果编码上限 1024";
            return false;
        }

        int radiusPx = Mathf.CeilToInt(guardRadius / config.worldSize * res);
        if (radiusPx <= 0 || radiusPx > 511)
        {
            whyNot = $"护卫半径 {guardRadius} 换算成 {radiusPx} 像素，超出 GPU 结果编码范围 1~511";
            return false;
        }

        return true;
    }

    bool ScanNearestTerritoryGPU(out Vector2 target)
    {
        target = default;
        var canvas = TerritoryCanvas.Instance;

        int res = config.resolution;
        float worldSize = config.worldSize;
        Vector2 pos = transform.position;

        int cx = Mathf.RoundToInt((pos.x / worldSize + 0.5f) * res);
        int cy = Mathf.RoundToInt((pos.y / worldSize + 0.5f) * res);
        int radiusPx = Mathf.CeilToInt(guardRadius / worldSize * res);
        if (radiusPx <= 0) return false;

        // 上一次的结果还在回读路上：本次不重复 dispatch，等它落地（期间沿用上一次的目标）
        guardScanPending = true;
        if (guardReadbackPending) return false;

        guardResultData[0] = uint.MaxValue;
        guardResultBuffer.SetData(guardResultData);

        guardCompute.SetTexture(guardKernel, "DataResult", canvas.DataRT);
        guardCompute.SetBuffer(guardKernel, "Result", guardResultBuffer);
        guardCompute.SetInt("_Resolution", res);
        guardCompute.SetInts("_SelfPos", cx, cy);
        guardCompute.SetInt("_SelfStage", owner.stage);
        guardCompute.SetInt("_RadiusPx", radiusPx);
        guardCompute.Dispatch(guardKernel, (res + 7) / 8, (res + 7) / 8, 1);

        guardScanPending = false;

        if (SystemInfo.supportsAsyncGPUReadback)
        {
            // 异步回读：不在主线程等 GPU，结果一两帧后由回调写入 guardTarget
            guardReadbackPending = true;
            guardReadbackStartTime = Time.realtimeSinceStartup;
            guardReadback = AsyncGPUReadback.Request(guardResultBuffer, OnGuardReadbackDone);
            LogGuardGpuOnce("异步回读");
            return false;
        }

        // 不支持异步回读时才同步取（会等 GPU 排空，但只读 4 字节）
        LogGuardGpuOnce("同步回读");
        guardResultBuffer.GetData(guardResultData);
        return DecodeGuardKey(guardResultData[0], res, worldSize, out target);
    }

    void LogGuardGpuOnce(string mode)
    {
        if (guardGpuLogged) return;
        guardGpuLogged = true;
    }

    void OnGuardReadbackDone(AsyncGPUReadbackRequest req)
    {
        guardReadbackPending = false;
        if (req.hasError) return;   // 出错就当这次没结果，下一次扫描重发

        var cfg = config != null ? config : MapConfig.Instance;
        if (cfg == null || guardResultData == null) return;

        uint key = req.GetData<uint>()[0];
        if (DecodeGuardKey(key, cfg.resolution, cfg.worldSize, out Vector2 t))
        {
            guardTarget = t;
            hasGuardTarget = true;
        }
        else
        {
            hasGuardTarget = false;
        }
    }

    /// <summary>把 GPU 写回的最小 key 解成世界坐标；key == uint.MaxValue 表示半径内没有敌方领土。</summary>
    static bool DecodeGuardKey(uint key, int res, float worldSize, out Vector2 target)
    {
        target = default;
        if (key == uint.MaxValue || res <= 0) return false;

        int px = (int)((key >> 10) & 0x3FF);
        int py = (int)(key & 0x3FF);
        target = new Vector2(
            ((px + 0.5f) / res - 0.5f) * worldSize,
            ((py + 0.5f) / res - 0.5f) * worldSize);
        return true;
    }

    bool ScanNearestTerritoryCPU(out Vector2 target)
    {
        target = default;
        var map = TerritoryCanvas.Instance.territoryMap;
        if (!map.IsCreated) return false;

        int res = config.resolution;
        float worldSize = config.worldSize;
        Vector2 pos = transform.position;

        int cx = Mathf.RoundToInt((pos.x / worldSize + 0.5f) * res);
        int cy = Mathf.RoundToInt((pos.y / worldSize + 0.5f) * res);
        int radiusPx = Mathf.CeilToInt(guardRadius / worldSize * res);
        if (radiusPx <= 0) return false;

        int x0 = Mathf.Max(0, cx - radiusPx);
        int x1 = Mathf.Min(res - 1, cx + radiusPx);
        int y0 = Mathf.Max(0, cy - radiusPx);
        int y1 = Mathf.Min(res - 1, cy + radiusPx);

        byte selfStage = (byte)owner.stage;
        float bestSq = guardRadius * guardRadius;
        int bestX = -1, bestY = -1;

        // 所有非本阵营领土（含 0 号黑色中立）都按距离平等竞争，谁近选谁
        for (int y = y0; y <= y1; y++)
        {
            int row = y * res;
            for (int x = x0; x <= x1; x++)
            {
                byte s = map[row + x];
                if (s == selfStage) continue;

                float wx = ((x + 0.5f) / res - 0.5f) * worldSize;
                float wy = ((y + 0.5f) / res - 0.5f) * worldSize;
                float dx = wx - pos.x;
                float dy = wy - pos.y;
                float sq = dx * dx + dy * dy;

                if (sq < bestSq)
                {
                    bestSq = sq;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        if (bestX < 0) return false;

        target = new Vector2(
            ((bestX + 0.5f) / res - 0.5f) * worldSize,
            ((bestY + 0.5f) / res - 0.5f) * worldSize);
        return true;
    }

    bool IsFacingTarget(Vector2 target)
    {
        Vector2 dir = target - (Vector2)transform.position;
        if (dir.sqrMagnitude < 1e-8f) return true;

        float targetAngle = currentAngle + Vector2.SignedAngle(transform.up, dir);
        if (!loop) targetAngle = ClampAngleToRange(targetAngle);

        return Mathf.Abs(Mathf.DeltaAngle(currentAngle, targetAngle)) <= 1f;
    }

    void RotateTo(Vector2 target, float turnSpeed)
    {
        Vector2 dir = target - (Vector2)transform.position;
        if (dir.sqrMagnitude < 1e-8f) return;

        // 用世界方向的夹角增量来算目标角，兼容父节点旋转
        float targetAngle = currentAngle + Vector2.SignedAngle(transform.up, dir);

        if (!loop)
        {
            targetAngle = ClampAngleToRange(targetAngle);
            currentAngle = Mathf.MoveTowardsAngle(currentAngle, targetAngle, turnSpeed * Time.deltaTime);
            currentAngle = Mathf.Clamp(currentAngle, angleRange.x, angleRange.y);
        }
        else
        {
            currentAngle = Mathf.MoveTowardsAngle(currentAngle, targetAngle, turnSpeed * Time.deltaTime);
            currentAngle = NormalizeAngle(currentAngle);
        }
    }

    float ClampAngleToRange(float angle)
    {
        float min = NormalizeAngle(angleRange.x);
        float max = NormalizeAngle(angleRange.y);
        float span = Mathf.Repeat(max - min, 360f);

        // 范围覆盖整圈或近似整圈，不需要夹取
        if (span >= 359.999f) return angle;

        float a = NormalizeAngle(angle);
        float rel = Mathf.Repeat(a - min, 360f);
        if (rel <= span + 0.001f) return a;

        float dMin = Mathf.Abs(Mathf.DeltaAngle(a, min));
        float dMax = Mathf.Abs(Mathf.DeltaAngle(a, max));
        return dMin <= dMax ? min : max;
    }

    float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle < 0f) angle += 360f;
        return angle;
    }
}
