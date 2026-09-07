using UnityEngine;

public class BallPainter : MonoBehaviour, IStageValue
{
    public SpriteRenderer sp;
    public Rigidbody2D rb;
    public Collider2D col;
    [field: SerializeField] public int stage { get; set; }
    public string guid = "";//场上注册时由 ItemType 分配，供伤害来源定位实体
    public int hurtSourceStage { get; set; }
    public string hurtSourceGuid { get; set; }
    public string hurtSourceDesc { get; set; }
    public HugeInt value { get; set; }

    public float baseWorldRadius = 0.5f;
    public float attackPower = 1.0f;
    public float ScaleChangeSpeed = 3f;

    public CurveTransform ScaleCurve;
    public CurveTransform SpeedCurve;
    public CurveTransform AcelerationCurve;
    public TrailRenderer TR;

    [Header("速度控制")]
    [Tooltip("从0加速到目标速度的时间")]
    public float fastA = 0.5f;
    [Tooltip("基于从0加速到目标速度计算所得的加速度，不过是负的")]
    public float slowA = 2f;
    [Tooltip("受击加速暂停")]
    public float hurt_fast_pause = 0.3f;

    private TerritoryCanvas canvas;
    private MapConfig config;
    private Vector2 lastWorldPos;
    private HugeInt lastValue = -1;
    private bool hasLast;
    private Vector2 lastMoveDir = Vector2.right;
    private float hurtPauseTimer;

    void Awake()
    {
        canvas = FindObjectOfType<TerritoryCanvas>();
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        rb = GetComponent<Rigidbody2D>();
        config = MapConfig.Instance;
    }

    void Start()
    {
        SetScaleMass();
        ColorSet();
        ColliderSet();
    }

    void FixedUpdate()
    {
        if (value <= 0) { Die(); return; }

        if (lastValue == -1 || lastValue != value)
        {
            lastValue = value;
            SetScaleMass();
        }

        float ts = transform.localScale.x;
        if (aimScale != -1 && ts != aimScale)
        {
            if (Mathf.Abs(ts - aimScale) <= 0.05f)
            {
                transform.localScale = Vector3.one * aimScale;
            }
            else
            {
                transform.localScale = Mathf.Lerp(ts, aimScale, Time.fixedDeltaTime * ScaleChangeSpeed) * Vector3.one;
            }
        }

        float targetSpeed = SpeedCurve.Evaluate(value);
        float curSpeed = rb.velocity.magnitude;
        if (curSpeed > 0.01f) lastMoveDir = rb.velocity / curSpeed;
        Vector2 dir = curSpeed > 0.01f ? rb.velocity / curSpeed : lastMoveDir;

        if (hurtPauseTimer > 0f)
        {
            hurtPauseTimer = Mathf.Max(0f, hurtPauseTimer - Time.fixedDeltaTime);
        }
        else
        {
            float speedError = targetSpeed - curSpeed;

            if (speedError > 0f && fastA > 0f)
            {
                float deltaV = (targetSpeed / fastA) * Time.fixedDeltaTime;
                if (deltaV > speedError) deltaV = speedError;
                rb.AddForce(dir * rb.mass * (deltaV / Time.fixedDeltaTime));
            }
            else if (speedError < 0f && slowA > 0f)
            {
                float brake = (targetSpeed / slowA) * Time.fixedDeltaTime;
                float needBrake = -speedError;
                if (brake > needBrake) brake = needBrake;
                rb.AddForce(-dir * rb.mass * (brake / Time.fixedDeltaTime));
            }
        }

        Vector2 cur = transform.position;
        float worldR = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y) * baseWorldRadius;
        float pixelR = worldR * (config.resolution / config.worldSize);

        int budget = value > int.MaxValue ? int.MaxValue : (int)((HugeInt)value).ToLong();

        Vector2 uvA = WorldToUV(lastWorldPos);
        Vector2 uvB = WorldToUV(cur);
        if (!hasLast) { uvA = uvB; hasLast = true; }

        int changed = canvas?.PaintSegment(uvA, uvB, stage, attackPower, pixelR, budget) ?? 0;
        Spend(changed);
        lastWorldPos = cur;
    }

    private float aimScale = -1;

    void SetScaleMass()
    {
        float s = ScaleCurve.Evaluate(value);
        aimScale = s;
        TR.widthMultiplier = s;
        rb.mass = value / 81920000f;
    }

    void ColorSet()
    {
        Color col = MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball);
        if(sp != null)sp.color = col;
        col += Color.white * 0.15f;
        col.a = 0.75f; TR.startColor = col;
        col.a = 0.25f; TR.endColor = col;
    }

    void ColliderSet()
    {
        if (Towel.AllTowel.TryGetValue(stage, out Towel t))
        {
            Physics2D.IgnoreCollision(col, t.shieldCollider);
            Physics2D.IgnoreCollision(col, t.towelCollider);
        }
    }

    void Die() { Destroy(gameObject); }

    public void Spend(int cost)
    {
        if (cost <= 0) return;
        HugeInt c = new HugeInt(cost);
        if (value <= c) value = 0;
        else value -= c;
        if (value == 0) Die();
    }

    public void AddValue(int amount)
    {
        if (amount <= 0) return;
        value += new HugeInt(amount);
    }

    public void WhileBeHit(int _stage, HugeInt _value)
    {
        // 只有受到实际伤害（数值减少）才暂停加速；治疗（_value<0）不算受击。
        if (_value > 0 && hurt_fast_pause > 0f)
            hurtPauseTimer = hurt_fast_pause;
    }

    Vector2 WorldToUV(Vector2 world)
    {
        float ms = config.worldSize;
        return new Vector2((world.x + ms / 2f) / ms, (world.y + ms / 2f) / ms);
    }

    void OnCollisionExit2D(Collision2D collision)
    {
        if (collision.collider.TryGetComponent(out IStageValue sv))
        {
            bool spawnEffect = true;
            if (collision.collider.CompareTag("Ball"))
            {
                // 确保两个大球碰撞只释放一次效果
                if (col != null && col.GetInstanceID() > collision.collider.GetInstanceID()) spawnEffect = false;
            }
            HugeInt max = (value > sv.value ? value : sv.value).Multiply(config.bounceRate);//mutiple
            //确保不会出现贷款
            max = (max > value) ? value : max;
            HugeInt cost = sv.Hit(stage, max, guid, $"{stage}号阵营大球");
            if (cost > 0)
            {
                if (spawnEffect) SpawnHitCrossEffect(cost, collision);
                string otherGuid = sv is BallPainter bp ? bp.guid : "";
                string otherDesc = sv is BallPainter ? $"{sv.stage}号阵营大球" : $"{sv.stage}号阵营实体";
                ((IStageValue)this).Hit(sv.stage, cost, otherGuid, otherDesc);
            }
        }
        if (value == 0) Die();
    }

    void SpawnHitCrossEffect(HugeInt hitValue, Collision2D collision)
    {
        EffectManager em = EffectManager.Instance;
        if (em == null || em.CE == null || hitValue <= 0) return;

        Vector2 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        Vector2 normal = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector2.zero;
        int count = Mathf.Max(1, Mathf.RoundToInt(HugeInt.Log2(hitValue)));
        Color col = sp != null ? sp.color : Color.white;

        em.Boom(new Vector3(point.x, point.y, 0f), col, count, 3f, 0.2f, 0.6f, normal, 30f);
    }
}
