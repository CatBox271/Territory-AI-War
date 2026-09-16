using UnityEngine;

/// <summary>
/// 穿甲弹（移植自 LifeGame 的 AllCross 机制）：物理弹体，靠穿过护盾直接击杀敌方炮塔。
///
/// 机制对应关系：
/// 1. 进入护盾不改变方向：对护盾/炮塔不做物理碰撞（启动时 Physics2D.IgnoreCollision 掉），
///    改成自己用 OverlapCircle 判定；盾内按每秒比例扣护盾值、同量消耗自身数值，
///    速度乘盾内减速比（对应 AllCross 的 get → speed*0.5，被盾"阻碍"而不是被挡下）。
/// 2. 离开护盾时方向随机偏转（对应 AllCross 的 RandomRotateAfterCrush）。
/// 3. 与敌方炮塔本体重叠即 Towel.Die 秒杀（取敌将首级），自身数值乘击杀消耗比后继续飞，可连杀
///    （对应 AllCross 的 Times /= 2; t.Die()）。
/// 4. 与大球的数值扣减不在这里做，交给大球默认逻辑（BallPainter.OnCollisionExit2D + MapConfig.bounceRate）。
/// 5. 不写领土网格 → 不涂地（对应 LifeGame 的"不能产生活细胞"）。
/// </summary>
public class PierceShell : MonoBehaviour, IStageValue
{
    [field: SerializeField] public int stage { get; set; }
    public int hurtSourceStage { get; set; }
    public string hurtSourceGuid { get; set; }
    public string hurtSourceDesc { get; set; }
    public HugeInt value { get; set; }

    /// <summary>这发弹的运行时指纹，用作伤害统计里的 guid。</summary>
    public string guid = "";

    [Header("引用")]
    public Rigidbody2D rb;
    public CircleCollider2D col;
    public SpriteRenderer sp;
    public TrailRenderer tr;

    [Header("飞行")]
    public float speed = 8f;
    /// <summary>质量口径与大球一致：rb.mass = 数值 / massDivisor，避免穿甲弹把大球顶飞。</summary>
    public float massDivisor = 81920000f;
    /// <summary>越出地图这个余量（世界单位）就自毁，防止没被墙拦住时一直飞。</summary>
    public float outOfBoundsMargin = 1f;

    [Header("护盾交互")]
    /// <summary>每秒扣护盾当前值的比例。</summary>
    public float shieldDrainPercentPerSecond = 0.25f;
    /// <summary>盾内速度倍率（照 AllCross：重叠护盾时速度 ×0.5）。</summary>
    [Range(0.05f, 1f)] public float shieldSlowFactor = 0.5f;
    /// <summary>离开护盾时的随机偏转角（±）。</summary>
    public float exitDeflectAngle = 30f;
    /// <summary>扣盾节流（秒）：避免每物理帧都记一次伤害。</summary>
    public float shieldHitInterval = 0.05f;

    [Header("击杀")]
    /// <summary>每次击杀炮塔后自身数值乘这个（照 AllCross：Times /= 2）。</summary>
    [Range(0.01f, 1f)] public float killCostRatio = 0.5f;

    [Header("外观")]
    /// <summary>精灵子物体朝向跟随速度（只转精灵，不转根节点，免得数值文本跟着歪）：贴图的「上」始终指向运动方向。</summary>
    public bool alignSpriteToVelocity = true;
    /// <summary>精灵额外角度补偿，用来对准原画朝向。</summary>
    public float spriteAngleOffset = 0f;

    private MapConfig config;
    /// <summary>当前所处的敌方护盾（null = 不在敌方护盾里）；离开时随机改向。</summary>
    private Towel insideShield;
    private float drainTimer;
    private Vector2 dir = Vector2.up;
    private bool launched;

    private static readonly Collider2D[] overlapBuffer = new Collider2D[32];

    /// <summary>判定半径：碰撞体半径 × 缩放。</summary>
    private float BodyRadius
    {
        get
        {
            if (col == null) return 0.05f;
            float s = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y));
            return Mathf.Max(col.radius * s, 0.01f);
        }
    }

    /// <summary>是否正在穿过敌方护盾（供调试/外部查询）。</summary>
    public bool IsInsideShield => insideShield != null;

    void Awake()
    {
        config = MapConfig.Instance;
        if (rb == null) TryGetComponent(out rb);
        if (col == null) TryGetComponent(out col);
        if (sp == null) sp = GetComponentInChildren<SpriteRenderer>();
        if (tr == null) tr = GetComponentInChildren<TrailRenderer>();
    }

    /// <summary>发射：带上数值、阵营、方向与速度，并关掉与全部护盾/炮塔的物理碰撞。</summary>
    public void Launch(HugeInt val, int ownerStage, Vector2 direction, float shellSpeed = -1f)
    {
        if (config == null) config = MapConfig.Instance;
        value = val < 0 ? HugeInt.Zero : val;
        stage = ownerStage;
        guid = ((uint)GetInstanceID()).ToString("x8");
        if (shellSpeed > 0f) speed = shellSpeed;
        dir = direction.sqrMagnitude > 0.000001f ? direction.normalized : (Vector2)transform.up;
        launched = true;
        insideShield = null;
        drainTimer = 0f;

        if (rb == null) TryGetComponent(out rb);
        if (col == null) TryGetComponent(out col);

        if (rb != null)
        {
            // 大数可能超出 float 范围，钳一下避免 Inf 质量把场地搅乱
            float m = value.ToFloat() / Mathf.Max(massDivisor, 1f);
            if (float.IsNaN(m) || float.IsInfinity(m) || m <= 0f) m = 0.0001f;
            rb.mass = Mathf.Clamp(m, 0.00001f, 100f);
            rb.velocity = dir * speed;
        }

        IgnoreShieldAndTowerCollisions();
        SetVisual();
        if (alignSpriteToVelocity) AlignSprite();   // 出膛第一帧就摆正，别先歪着飞一下
    }

    /// <summary>启动时把与场上所有护盾、炮塔（含己方）的物理碰撞关掉：穿盾靠判定，不靠物理。</summary>
    void IgnoreShieldAndTowerCollisions()
    {
        if (col == null) return;
        foreach (var t in Towel.AllTowel.Values)
        {
            if (t == null) continue;
            if (t.shieldCollider != null) Physics2D.IgnoreCollision(col, t.shieldCollider);
            if (t.towelCollider != null) Physics2D.IgnoreCollision(col, t.towelCollider);
        }
    }

    void SetVisual()
    {
        Color c = config != null ? config.GetColor(stage, MapConfig.ColorStage.Bullet) : Color.white;
        if (sp != null)
        {
            sp.color = c;
            if (!alignSpriteToVelocity)
                sp.transform.localEulerAngles = new Vector3(0f, 0f, spriteAngleOffset);
        }
        if (tr != null)
        {
            Color a = c + Color.white * 0.15f;
            a.a = 0.75f; tr.startColor = a;
            a.a = 0.25f; tr.endColor = a;
        }
    }

    void FixedUpdate()
    {
        if (!launched || config == null) return;

        if (value <= 0) { Destroy(gameObject); return; }

        TrackShieldAndTowers();
        MaintainSpeed();
        if (alignSpriteToVelocity) AlignSprite();
        if (OutOfBounds()) Destroy(gameObject);
    }

    /// <summary>每物理帧判定：在不在敌方护盾里（进出状态）、有没有撞上敌方炮塔本体。</summary>
    void TrackShieldAndTowers()
    {
        Towel shieldOwner = null;
        int n = Physics2D.OverlapCircleNonAlloc(transform.position, BodyRadius, overlapBuffer);

        for (int i = 0; i < n; i++)
        {
            Collider2D other = overlapBuffer[i];
            if (other == null || other == col) continue;

            // 护盾：只认敌方的、还有盾值的
            if (other.TryGetComponent(out ShieldEffect _))
            {
                Towel owner = other.GetComponentInParent<Towel>();
                if (owner != null && owner.stage != stage && owner.shield_value > 0)
                    shieldOwner = owner;
                continue;
            }

            // 炮塔本体：命中即秒杀（穿盾取首级）；无敌窗口与已死的塔跳过
            if (other.TryGetComponent(out Towel tower))
            {
                if (tower.stage == stage || tower.isDead || tower.IsInvincible) continue;
                KillTower(tower);
            }
        }

        if (shieldOwner != insideShield)
        {
            // 进入护盾不改向，离开护盾随机改向（照 AllCross 的语义）
            if (insideShield != null && shieldOwner == null) DeflectOnShieldExit();
            insideShield = shieldOwner;
            drainTimer = 0f;
        }

        if (insideShield != null) DrainShield(insideShield);
    }

    /// <summary>盾内按比例扣护盾值，同量消耗自身数值；盾碎立刻开无敌。</summary>
    void DrainShield(Towel tower)
    {
        drainTimer += Time.fixedDeltaTime;
        if (drainTimer < shieldHitInterval) return;

        float dt = drainTimer;
        drainTimer = 0f;

        IStageValue shieldSV = tower.shieldSV;
        if (shieldSV == null) return;

        HugeInt before = tower.shield_value;
        if (before <= 0) return;

        HugeInt drain = before.Multiply(shieldDrainPercentPerSecond * dt);
        if (drain <= 0) drain = 1;   // 至少扣 1：盾一定被穿甲啃穿，只是快慢问题

        HugeInt cost = shieldSV.Hit(stage, drain, guid, $"{stage}号阵营穿甲弹");
        if (cost > 0)
        {
            value -= cost;
            if (value < 0) value = HugeInt.Zero;
        }

        if (tower.shield_value <= 0) tower.OnShieldBroken();
    }

    /// <summary>命中敌方炮塔本体：秒杀，自身数值按击杀消耗比减一档后继续飞。</summary>
    void KillTower(Towel tower)
    {
        tower.Die(stage, "穿甲弹");
        value = value.Multiply(killCostRatio);
        if (value < 0) value = HugeInt.Zero;
    }

    /// <summary>离开护盾：方向随机偏转（照 AllCross 的随机转向），并把新方向立刻写回刚体——
    /// 否则紧接着的 MaintainSpeed 会从旧速度反推方向，把这次偏转抹掉。</summary>
    void DeflectOnShieldExit()
    {
        if (exitDeflectAngle <= 0f) return;
        float angle = Random.Range(-exitDeflectAngle, exitDeflectAngle);
        dir = Quaternion.AngleAxis(angle, Vector3.forward) * dir;
        if (rb != null) rb.velocity = dir * speed;
    }

    /// <summary>维持速度：撞墙/被大球弹开后照样按 speed 飞，盾内速度乘减速比。</summary>
    void MaintainSpeed()
    {
        if (rb == null) return;
        Vector2 v = rb.velocity;
        Vector2 d = v.sqrMagnitude > 0.000001f ? v.normalized : dir;
        dir = d;
        float target = insideShield != null ? speed * shieldSlowFactor : speed;
        rb.velocity = d * target;
    }

    void AlignSprite()
    {
        if (sp == null || dir.sqrMagnitude < 0.000001f) return;
        float z = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f + spriteAngleOffset;
        sp.transform.rotation = Quaternion.Euler(0f, 0f, z);
    }

    bool OutOfBounds()
    {
        float half = config.worldSize * 0.5f + Mathf.Max(0f, outOfBoundsMargin);
        Vector2 p = transform.position;
        return Mathf.Abs(p.x) > half || Mathf.Abs(p.y) > half;
    }

    /// <summary>被大球默认逻辑扣数值时回调（大球侧负责播命中特效）。</summary>
    public void WhileBeHit(int _stage, HugeInt _value) { }
}
