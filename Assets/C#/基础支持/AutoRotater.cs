using UnityEngine;

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

    void Start()
    {
        RandomWay("start");
        currentAngle = NormalizeAngle(transform.localEulerAngles.z);
        owner = GetComponentInParent<Towel>();
        config = MapConfig.Instance;

        if (guardCompute != null && owner != null)
        {
            guardKernel = guardCompute.FindKernel("CSFindNearest");
            guardResultBuffer = new ComputeBuffer(1, sizeof(uint));
            guardResultData = new uint[1];
        }
    }

    void OnDestroy()
    {
        if (guardResultBuffer != null)
        {
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
        if (guardMode && TryGetGuardTarget(out Vector2 target))
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
        return true;
    }

    bool TryGetGuardTarget(out Vector2 target)
    {
        target = default;
        if (owner == null || config == null) return false;
        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return false;

        // 有目标时按间隔重新扫描，避免每帧全图查询
        if (hasGuardTarget && Time.time < nextGuardScanTime)
        {
            target = guardTarget;
            return true;
        }

        hasGuardTarget = ScanNearestEnemyTerritory(out guardTarget);
        nextGuardScanTime = Time.time + guardUpdateInterval;

        if (!hasGuardTarget) return false;
        target = guardTarget;
        return true;
    }

    bool ScanNearestEnemyTerritory(out Vector2 target)
    {
        // 优先走 GPU；没配置 shader / 纹理不可用时退回 CPU 逻辑
        if (guardCompute != null
            && guardKernel >= 0
            && guardResultBuffer != null
            && TerritoryCanvas.Instance != null
            && TerritoryCanvas.Instance.DataRT != null)
        {
            return ScanNearestTerritoryGPU(out target);
        }
        print("GPU距离计算未启用");
        return ScanNearestTerritoryCPU(out target);
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

        guardResultData[0] = uint.MaxValue;
        guardResultBuffer.SetData(guardResultData);

        guardCompute.SetTexture(guardKernel, "DataResult", canvas.DataRT);
        guardCompute.SetBuffer(guardKernel, "Result", guardResultBuffer);
        guardCompute.SetInt("_Resolution", res);
        guardCompute.SetInts("_SelfPos", cx, cy);
        guardCompute.SetInt("_SelfStage", owner.stage);
        guardCompute.SetInt("_RadiusPx", radiusPx);
        guardCompute.Dispatch(guardKernel, (res + 7) / 8, (res + 7) / 8, 1);

        guardResultBuffer.GetData(guardResultData);
        uint key = guardResultData[0];
        if (key == uint.MaxValue) return false;

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
