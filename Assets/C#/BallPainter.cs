using UnityEngine;

public class BallPainter : MonoBehaviour, IStageValue
{
    private SpriteRenderer sp;
    public Rigidbody2D rb;
    public Collider2D col;
    [field: SerializeField] public int stage { get; set; }
    public HugeInt value { get; set; }

    public float baseWorldRadius = 0.5f;
    public float attackPower = 1.0f;
    public int HitDivide = 3;

    public CurveTransform ScaleCurve;
    public CurveTransform SpeedCurve;
    public CurveTransform AcelerationCurve;
    public TrailRenderer TR;

    private TerritoryCanvas canvas;
    private MapConfig config;
    private Vector2 lastWorldPos;
    private HugeInt lastValue = -1;
    private bool hasLast;

    void Awake()
    {
        canvas = FindObjectOfType<TerritoryCanvas>();
        sp = GetComponent<SpriteRenderer>();
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
        if (value <= 0) return;

        if (lastValue == -1 || lastValue != value)
        {
            lastValue = value;
            SetScaleMass();
        }

        float aimSpeed = SpeedCurve.Evaluate(value);
        float curSpeed = rb.velocity.magnitude;
        Vector2 dir = curSpeed > 0 ? rb.velocity / curSpeed : Vector2.zero;

        if (curSpeed < aimSpeed)
            rb.AddForce(dir * rb.mass * AcelerationCurve.Evaluate(value));
        else if (curSpeed > aimSpeed * 1.25f)
            rb.AddForce(dir * rb.mass * (aimSpeed - curSpeed) * 0.25f);

        Vector2 cur = transform.position;
        float worldR = Mathf.Max(transform.lossyScale.x, transform.lossyScale.y) * baseWorldRadius;
        float pixelR = worldR * (config.resolution / config.worldSize);

        int budget = value > int.MaxValue ? int.MaxValue : (int)((HugeInt)value).ToLong();

        Vector2 uvA = WorldToUV(lastWorldPos);
        Vector2 uvB = WorldToUV(cur);
        if (!hasLast) { uvA = uvB; hasLast = true; }

        int changed = canvas.PaintSegment(uvA, uvB, stage, attackPower, pixelR, budget);
        Spend(changed);
        lastWorldPos = cur;
    }

    void SetScaleMass()
    {
        float s = ScaleCurve.Evaluate(value);
        transform.localScale = Vector3.one * s;
        TR.widthMultiplier = s;
        rb.mass = (value / 81920000).ToLong();
    }

    void ColorSet()
    {
        Color col = MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball);
        sp.color = col;
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

    public void WhileBeHit(int _stage, HugeInt _value) { }

    Vector2 WorldToUV(Vector2 world)
    {
        float ms = config.worldSize;
        return new Vector2((world.x + ms / 2f) / ms, (world.y + ms / 2f) / ms);
    }

    void OnCollisionExit2D(Collision2D collision)
    {
        if (collision.collider.TryGetComponent(out IStageValue sv))
            value -= sv.Hit(stage, value / HitDivide);
    }
}
