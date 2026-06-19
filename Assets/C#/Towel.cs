using System.Collections.Generic;
using UnityEngine;

public class Towel : MonoBehaviour, IStageValue
{
    [field: SerializeField] public int stage { get; set; }
    public long _value;
    public HugeInt value { get; set; }

    public GameObject shield;
    private IStageValue _shieldSV;
    public IStageValue shieldSV => _shieldSV ??= shield.GetComponent<IStageValue>();

    public long _shield_value = 1048576;
    public HugeInt shield_value { get => shieldSV.value; set => shieldSV.value = value; }

    public float Radius;
    private SpriteRenderer sp;
    private MapConfig config;
    private TerritoryCanvas canvas;

    public float bulletSpeed = 3f;
    public float bigBallSpeed = 1f;
    public CurveTransform bulletCount;
    public CurveTransform bulletInterval;
    public CurveTransform shieldRadius;
    public CurveTransform bulletRandomSpeed;

    public static Dictionary<int, Towel> AllTowel = new();
    public Collider2D towelCollider;
    public Collider2D shieldCollider;

    void Awake()
    {
        value = _value;
        shield_value = _shield_value;
        TryGetComponent(out sp);
        config = MapConfig.Instance;
        canvas = FindObjectOfType<TerritoryCanvas>();
        AllTowel[stage] = this;
    }

    float clock;
    Vector2 lastFireDirection;

    void Start()
    {
        lastFireDirection = transform.up;
        sp.color = config.GetColor(stage, MapConfig.ColorStage.Towel);
        value = config.TowelDefaultBullets;
        LookAt(Vector3.zero);
        PaintInitialCircle();

        ShotGun(1048576, 60, 1024);
    }

    void Update()
    {
        if (isDead) return;
        float interval = bulletInterval.Evaluate(value);
        clock += Time.deltaTime;

        int fireCount = 0;
        while (clock > interval)
        {
            clock -= interval;
            fireCount++;
            if (interval <= 0) break;
        }

        if (fireCount > 0)
        {
            Vector2 currentDir = transform.up;
            float totalAngle = Vector2.SignedAngle(lastFireDirection, currentDir);

            for (int i = 0; i < fireCount; i++)
            {
                float t = fireCount == 1 ? 1f : (float)i / (fireCount - 1);
                Vector2 dir = Quaternion.AngleAxis(totalAngle * t, Vector3.back) * lastFireDirection;
                FireWithDir(dir);
            }

            lastFireDirection = currentDir;
        }

        ShieldTransform();
        ShotGunTest();
    }

    void CreateExplosionEffect()
    {
        var go = new GameObject("TowelExplosion");
        go.transform.position = transform.position;
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration = 1.5f;
        main.startLifetime = 1.5f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 3f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
        main.startColor = config.GetColor(stage, MapConfig.ColorStage.Towel);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.loop = false;
        main.stopAction = ParticleSystemStopAction.Destroy;
        var emission = ps.emission;
        emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 40) });
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.01f;
        var vol = ps.velocityOverLifetime;
        vol.enabled = true;
        vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0)));
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(config.GetColor(stage, MapConfig.ColorStage.Towel), 0f), new GradientColorKey(config.GetColor(stage, MapConfig.ColorStage.Towel), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
        );
        col.color = grad;
        ps.Play();
    }

    private bool isDead;


    public void Die()
    {
        if (isDead) return;
        isDead = true;

        if (shield != null) shield.SetActive(false);

        CreateExplosionEffect();
        AllTowel.Remove(stage);
        StartCoroutine(DieSequence());
    }

    System.Collections.IEnumerator DieSequence()
    {
        var marbles = FindObjectsOfType<Marble>();
        foreach (var marble in marbles)
        {
            if (marble.ValueExponent > 0)
            {
                SpawnBigBall(HugeInt.Pow(2, (int)marble.ValueExponent));
                yield return new WaitForSeconds(0.2f);
            }
        }
        if (value > 0)
        {
            SpawnBigBall(value);
            yield return new WaitForSeconds(0.2f);
        }
        if (MarbleManager.Instance != null)
            MarbleManager.Instance.OnTeamDeath(stage);
        Destroy(gameObject);
    }

    public void WhileBeHit(int _stage, HugeInt _value)
    {
        Debug.Log($"stage:{stage} has killed by stage{_stage}");
        if (value <= 0) Die();
    }

    void ShotGunTest()
    {
        if (Input.GetKeyDown(KeyCode.S))
        {
            //LookAt(Vector3.zero);
            ShotGun(1048576);
        }
    }

    public float BulletPosRandom = 0.05f;

    void FireWithDir(Vector2 dir)
    {
        if (value <= 0) return;
        int bv = (int)bulletCount.Evaluate(value);
        if (bv > value) bv = (int)value.ToLong();
        value -= bv;
        var pos = (Vector2)transform.position + Random.insideUnitCircle * BulletPosRandom;
        float maxAngle = bulletRandomSpeed.Evaluate(value);
        var finalDir = (Vector2)(Quaternion.AngleAxis(Random.Range(-maxAngle, maxAngle), Vector3.forward) * dir);
        BulletManager.Instance.Fire(pos, finalDir, stage, bv, bulletSpeed);
    }

    void ShieldTransform()
    {
        if (shield_value <= 0)
        {
            if (shield.activeSelf) shield.SetActive(false);
        }
        else
        {
            if (!shield.activeSelf)
            {
                shield.SetActive(true);
                var s = shield.GetComponent<ShieldEffect>();
                s.sp.color = s.originColor;
            }
                shield.transform.localScale = Vector3.one * shieldRadius.Evaluate(shield_value);

        }
    }

    void PaintInitialCircle()
    {
        if (canvas == null || config == null) return;
        Vector2 wp = transform.position;
        float ms = config.worldSize;
        Vector2 uv = new Vector2((wp.x + ms / 2f) / ms, (wp.y + ms / 2f) / ms);
        float pr = Radius * (config.resolution / ms);
        canvas.PaintSegment(uv, uv, stage, float.MaxValue, pr, int.MaxValue);
    }

    void LookAt(Vector3 pos)
    {
        var dir = pos - transform.position;
        transform.localEulerAngles += new Vector3(0, 0, Vector2.SignedAngle(transform.up, dir));
    }



    public void SpawnBigBall(HugeInt val)
    {
        if (config.basicBallPrefab == null) return;
        var ob = Instantiate(config.basicBallPrefab, transform.position, Quaternion.identity);
        var se = ob.GetComponent<StageEditor>();
        if (se != null) { se.enabled = false; Destroy(se); }
        var bp = ob.GetComponent<BallPainter>();
        if (bp != null)
        {
            bp.stage = stage;
            bp.value = val;
        }
        var rb = ob.GetComponent<Rigidbody2D>();
        if (rb != null) {
        var dv = ob.GetComponentInChildren<DisplayValue>();
        if (dv != null)
            dv.SetOutline(Color.black, 0.2f);

                rb.velocity = transform.up * bigBallSpeed;
        }
    }

    public void ShotGun(HugeInt val,float angle = 0,int defaultNum = 0,int minVal = 0,int maxVal = 0)
    {
        if (angle == 0) angle = config.ShotGunAngle;
        if (defaultNum == 0) defaultNum = config.ShotGunBulletNum;
        if (minVal == 0) minVal = config.ShotGunMinVal;
        if (maxVal == 0) maxVal = config.ShotGunMaxVal;

        if (val / minVal < defaultNum) defaultNum = (int)(val / minVal).ToLong();
        else if (val / defaultNum > maxVal) defaultNum = (int)(val / maxVal).ToLong();
        int bv = (int)(val / defaultNum).ToLong();
        float da = angle / (defaultNum + 1);
        float sa = angle * -0.5f;
        for (int i = 1; i <= defaultNum; i++)
        {
            var dir = Quaternion.AngleAxis(sa + da * i, Vector3.back) * transform.up;
            BulletManager.Instance.Fire(transform.position, dir, stage, bv, bulletSpeed);
        }
    }
}
