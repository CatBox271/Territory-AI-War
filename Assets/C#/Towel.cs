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
    public CurveTransform bulletCount;
    public CurveTransform bulletInterval;
    public CurveTransform shieldRadius;

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
        LookAt(Vector3.zero);
        PaintInitialCircle();

        ShotGun(1048576, 360, 1024);
    }

    void Update()
    {
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

    public void WhileBeHit(int _stage, HugeInt _value)
    {
        //±»»÷É±Ê±´¥·¢
        Debug.Log($"stage:{stage} has killed by stage{_stage}");
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
        BulletManager.Instance.Fire(pos, dir, stage, bv, bulletSpeed);
    }

    void ShieldTransform()
    {
        if (shield_value <= 0)
        {
            if (shield.activeSelf) shield.SetActive(false);
        }
        else
        {
            if (!shield.activeSelf) shield.SetActive(true);
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



    void ShotGun(HugeInt val,float angle = 0,int defaultNum = 0,int minVal = 0,int maxVal = 0)
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
