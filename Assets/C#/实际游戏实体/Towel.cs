using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;



public class Towel : MonoBehaviour, IStageValue
{
    [field: SerializeField] public int stage { get; set; }
    public int hurtSourceStage { get; set; }
    public string hurtSourceGuid { get; set; }
    public string hurtSourceDesc { get; set; }
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
    /// <summary>
    /// 从1开始
    /// </summary>
    public static Dictionary<int, Towel> AllTowel = new();
    public Collider2D towelCollider;
    public Collider2D shieldCollider;
    public MessageDisplayer messageDisplayer;
    /// <summary>ShowTMP 预制：直接显示的飘字（缩放出现 → 漂向 y=0 → 淡出）</summary>
    public GameObject tipPrefab;
    public AimController aimController;

    [Header("炮塔升级")]
    public int turretUpgraded;
    public int shieldUpgradeOwned;
    public float shieldBreakInvincibleTime = 2f;
    public float upgradedBulletRadiusScale = 1.7f;
    public float upgradedBulletImpactScale = 1.6f;
    private float invincibleUntil = float.MinValue;
    private float baseGuardSpeed = -1f;
    public bool IsInvincible => shieldUpgradeOwned > 0 && Time.time < invincibleUntil;

    /// <summary>无敌窗口剩余秒数；不在无敌中时为 0。</summary>
    public float InvincibleRemaining => IsInvincible ? Mathf.Max(0f, invincibleUntil - Time.time) : 0f;

    [Header("无敌闪烁")]
    /// <summary>无敌期间是否让塔身闪烁（走 Towel玻璃 那份 shader 的 _InvincibleGlow 闪白 Pass）</summary>
    public bool invincibleBlink = true;
    /// <summary>每秒亮灭多少个周期</summary>
    public float invincibleBlinkSpeed = 3f;
    /// <summary>峰值闪白强度：1 = 材质里的闪白颜色原样叠加，>1 更刺眼（闪白颜色本身可填 HDR）</summary>
    [Range(0f, 4f)] public float invincibleBlinkPeak = 1f;
    /// <summary>一个周期里"亮"的占比</summary>
    [Range(0.05f, 0.95f)] public float invincibleBlinkDuty = 0.5f;
    /// <summary>过渡宽度（占周期比例）：0.001 = 硬闪方波，越大越像呼吸；实际会被压到不超过 duty 的一半</summary>
    [Range(0.001f, 0.5f)] public float invincibleBlinkSoftness = 0.15f;
    /// <summary>手动相位偏移（0~1，1 = 整整错开一个周期）</summary>
    [Range(0f, 1f)] public float invincibleBlinkPhase = 0f;
    /// <summary>勾上则每座塔按自己的实例 ID 错开相位（不消耗 Random，不影响其它随机数）</summary>
    public bool invincibleBlinkRandomPhase = false;

    private static readonly int InvincibleGlowID = Shader.PropertyToID("_InvincibleGlow");
    private MaterialPropertyBlock blinkBlock;
    private float blinkGlow = -1f;
    private float blinkPhase;

    /// <summary>把闪白强度写进塔身渲染器：0=原样玻璃，越大越亮（shader 第二个 Pass 加法叠白，不动塔的 alpha）。只在值变化时写，不新建材质实例、不动共享材质。</summary>
    void SetInvincibleGlow(float glow)
    {
        if (sp == null || glow == blinkGlow) return;
        blinkGlow = glow;
        blinkBlock ??= new MaterialPropertyBlock();
        sp.GetPropertyBlock(blinkBlock);
        blinkBlock.SetFloat(InvincibleGlowID, glow);
        sp.SetPropertyBlock(blinkBlock);
    }

    /// <summary>无敌窗口内闪烁：每个周期前 duty 段"亮"（叠白加亮，不改塔的透明度），两侧各留 softness 宽度做平滑过渡（softness 压到最小就退化成硬闪方波）；窗口一结束立刻还原成 0。</summary>
    void UpdateInvincibleBlink()
    {
        float glow = 0f;
        if (invincibleBlink && IsInvincible && invincibleBlinkSpeed > 0.001f)
        {
            float period = 1f / invincibleBlinkSpeed;
            float wave = Mathf.Repeat(Time.time / period + blinkPhase, 1f);
            float duty = invincibleBlinkDuty;
            float edge = Mathf.Clamp(invincibleBlinkSoftness, 0.001f, duty * 0.5f);   // 保证亮段两侧的过渡不重叠
            float fadeIn = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(wave / edge));
            float fadeOut = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((wave - (duty - edge)) / edge));
            // 量化到 1/32：过渡段照样顺滑，但 MPB 只在跨过一档时才写（不每帧写）
            glow = Mathf.Round(Mathf.Min(fadeIn, fadeOut) * 32f) / 32f * Mathf.Max(0f, invincibleBlinkPeak);
        }
        SetInvincibleGlow(glow);
    }

    /// <summary>每级炮塔升级叠加上去的子弹显示半径倍率：1 + (倍率-1)  等级。</summary>
    public float CurrentBulletRadiusScale => 1f + (upgradedBulletRadiusScale - 1f) * Mathf.Max(0, turretUpgraded);
    /// <summary>每级炮塔升级叠加上去的子弹动量倍率：1 + (倍率-1)  等级。</summary>
    public float CurrentBulletImpactScale => 1f + (upgradedBulletImpactScale - 1f) * Mathf.Max(0, turretUpgraded);
    /// <summary>每级炮塔升级让极限转速再翻一倍。</summary>
    public float CurrentGuardSpeedScale => Mathf.Pow(2f, Mathf.Max(0, turretUpgraded));

    void Awake()
    {
        value = _value;
        shield_value = _shield_value;
        TryGetComponent(out sp);
        // 错相位：随机模式用实例 ID 散列（黄金比小数），确定性、不碰 UnityEngine.Random
        blinkPhase = invincibleBlinkRandomPhase
            ? Mathf.Repeat((Mathf.Abs(GetInstanceID()) % 997) * 0.6180339887f, 1f)
            : Mathf.Repeat(invincibleBlinkPhase, 1f);
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
        PaintInitialCircle();
        LookAt(Random.insideUnitCircle / 100f);
        ShotGun(1048576, 60, 1024);

        Say("HelloWorld");

        //InformGeter初始化
        InformGetter.AddItem(stage, new ItemType(transform, "炮塔基地"));
        InformGetter.AddItem(stage, new ItemType(shield.transform, "基地护盾", shield.GetComponent<IStageValue>()));
    }

    void Update()
    {
        if (isDead) return;
        UpdateInvincibleBlink();
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

    public bool Say(string content, bool force = false) => messageDisplayer.Say(content, force);

    private Transform _tipPool;
    private Transform tipPool
    {
        get
        {
            if (_tipPool == null)
            {
                GameObject pool = GameObject.FindGameObjectWithTag("TextPool");
                if (pool != null) _tipPool = pool.transform;
            }
            return _tipPool;
        }
    }

    /// <summary>这座炮塔上还在播的飘字（播完自销毁后会被清掉）</summary>
    private readonly List<TowelTip> activeTips = new List<TowelTip>();
    /// <summary>同一座炮塔上多条飘字之间的竖直空隙（世界单位）</summary>
    public float tipStackGap = 0.25f;
    /// <summary>量不到上一条飘字高度时，按这个高度给它让位（世界单位）</summary>
    public float tipFallbackHeight = 0.6f;

    /// <summary>
    /// 直接显示一条飘字（ShowTMP）：从炮塔位置缩放出现，缓慢漂向 y=0，停留后淡出。
    /// 每条飘字各建一份独立实例，同一座炮塔上同时存在的多条沿 y 上下排开，不再互相顶掉。
    /// 没配 tipPrefab 时退回原来的逐字飘字。
    /// </summary>
    public void ShowTip(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        if (tipPrefab == null)
        {
            Say(content, true);
            return;
        }

        // 清掉已经播完（自销毁）的实例，把它们占的位置腾出来
        for (int i = activeTips.Count - 1; i >= 0; i--)
        {
            if (activeTips[i] == null || !activeTips[i].IsPlaying) activeTips.RemoveAt(i);
        }

        Transform pool = tipPool;
        GameObject go = pool != null ? Instantiate(tipPrefab, pool) : Instantiate(tipPrefab);
        TowelTip newTip = go.GetComponent<TowelTip>();
        if (newTip == null)
        {
            Destroy(go);
            return;
        }

        Color color = config != null ? config.GetColor(stage, MapConfig.ColorStage.Bright) : Color.white;
        newTip.Play(content, color, transform.position);
        newTip.SetStackOffset(NextStackOffset(newTip));
        activeTips.Add(newTip);
    }

    /// <summary>
    /// 给新来的这条算竖直错开量：把已经在播的每一条的高度 + 空隙累加起来，
    /// 朝远离漂移终点（yTarget = 0）的方向排，避免堆到场地中间去。
    /// 每一步取「上一条高度」与「这条高度」的较大值，保证上下两条不重叠。
    /// </summary>
    private float NextStackOffset(TowelTip incoming)
    {
        float gap = tipStackGap > 0f ? tipStackGap : 0.25f;
        float fallback = tipFallbackHeight > 0f ? tipFallbackHeight : 0.6f;
        float inH = incoming != null ? incoming.WorldHeight : 0f;
        if (inH <= 0f) inH = fallback;

        float used = 0f;
        foreach (TowelTip t in activeTips)
        {
            if (t == null || !t.IsPlaying) continue;
            float h = t.WorldHeight;
            if (h <= 0f) h = fallback;
            used += Mathf.Max(h, inH) + gap;
        }
        if (used <= 0f) return 0f;

        float target = incoming != null ? incoming.yTarget : 0f;
        float dir = transform.position.y >= target ? 1f : -1f;
        return dir * used;
    }

    /// <summary>选择 2：炮塔升级。后坐力带来的子弹显示半径与动量由开火参数处理；这里翻倍自动护卫的极限转速，常态转速不动。</summary>
    public void ApplyTurretUpgrade()
    {
        turretUpgraded++;
        if (aimController != null && aimController.auto != null)
        {
            if (baseGuardSpeed < 0f) baseGuardSpeed = aimController.auto.guard_speed;
            aimController.auto.guard_speed = baseGuardSpeed * CurrentGuardSpeedScale;
        }
    }


    /// <summary>选择 3：护盾升级。护盾破碎后获得一段无视子弹与大球伤害的时间。</summary>
    public void ApplyShieldUpgrade()
    {
        shieldUpgradeOwned++;
        if (shield == null) return;
        Transform shieldPic = shield.transform.Find("ShieldPic");
        if (shieldPic == null) return;
        Transform clone = Instantiate(shieldPic, shieldPic.parent);
        clone.localScale -= Vector3.one * (0.15f * shieldUpgradeOwned);
    }

    /// <summary>护盾被击碎（或检测到已碎）时调用，立即开启无敌窗口与发光。</summary>
    public void OnShieldBroken()
    {
        if (shield != null && shield.activeSelf) shield.SetActive(false);
        if (shieldUpgradeOwned <= 0) return;

        invincibleUntil = Time.time + shieldBreakInvincibleTime * shieldUpgradeOwned;
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

    public bool isDead;

    private int killerStage = -1;
    private string killerWeapon = "";

    public void Die(int killerStage = -1, string killerWeapon = "")
    {
        if (isDead) return;
        isDead = true;
        SetInvincibleGlow(0f); // 死亡序列期间别再闪（Update 已停，这里手动把闪白还原成 0）
        this.killerStage = killerStage;
        this.killerWeapon = killerWeapon ?? "";

        if (shield != null) shield.SetActive(false);

        CreateExplosionEffect();
        StartCoroutine(DieSequence());
    }

    System.Collections.IEnumerator DieSequence()
    {
        // 遗言放在死亡流程最前面：先说完遗言，再开始释放大球
        var lastWords = AIAgent.OnStageDeathAsync(stage, killerStage, killerWeapon);
        while (!lastWords.IsCompleted) yield return null;
        yield return new WaitForSeconds(1.5f); // 遗言气泡至少显示一会儿，避免立即销毁导致看不到

        var marbles = FindObjectsOfType<Marble>();
        foreach (var marble in marbles)
        {
            if (marble.ValueExponent > 0)
            {
                SpawnBigBall(HugeInt.Pow(2, (int)marble.ValueExponent));
                yield return new WaitForSeconds(0.2f);
            }
        }

        // 死亡时把道具栈里的道具也以大球形式释放
        if (config != null && config.teamProps != null && stage >= 0 && stage < config.teamProps.Length)
        {
            List<PropEntry> props = config.teamProps[stage];
            if (props != null)
            {
                foreach (PropEntry prop in new List<PropEntry>(props))
                {
                    SpawnBigBall(prop.value);
                    yield return new WaitForSeconds(0.2f);
                }
                props.Clear();
            }
        }

        if (value > 0)
        {
            SpawnBigBall(value);
            yield return new WaitForSeconds(0.2f);
        }

        if (MarbleManager.Instance != null)
            MarbleManager.Instance.OnTeamDeath(stage);

        AllTowel.Remove(stage);
        Destroy(gameObject);
    }

    public HugeInt Hit(int _stage, HugeInt _value, string sourceGuid = "", string sourceDesc = "")
    {
        if (IsInvincible) return 0; // 无敌时间内完全无视伤害

        HugeInt cost = 0;
        if (stage == _stage) return cost;
        hurtSourceStage = _stage;
        hurtSourceGuid = sourceGuid;
        hurtSourceDesc = sourceDesc;

        if (value > _value)
        {
            cost = _value;
            WhileBeHit(_stage, cost);
            value -= cost;
        }
        else
        {
            cost = value;
            WhileBeHit(_stage, cost);
            value = 0;
        }
        InformGetter.AddDamage(stage, _stage, sourceGuid, sourceDesc, cost);
        return cost;
    }

    public void WhileBeHit(int _stage, HugeInt _value)
    {
        if (IsInvincible) return;
        Debug.Log($"stage:{stage} has killed by stage{_stage}");
        Die(_stage, ExtractWeapon(hurtSourceDesc));
    }

    private static string ExtractWeapon(string sourceDesc)
    {
        if (string.IsNullOrEmpty(sourceDesc)) return "";
        int idx = sourceDesc.IndexOf("号阵营");
        if (idx >= 0) return sourceDesc.Substring(idx + 3).Trim();
        return sourceDesc;
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

        float displayRadius = -1f;
        float impactScale = 1f;
        if (turretUpgraded > 0)
        {
            displayRadius = (BulletManager.Instance != null ? BulletManager.Instance.bulletDisplayRadius : 2f) * CurrentBulletRadiusScale;
            impactScale = CurrentBulletImpactScale;
        }

        BulletManager.Instance.Fire(pos, finalDir, stage, bv, config != null ? config.NormalBulletSpeed : bulletSpeed, displayRadius, impactScale);
    }

    void ShieldTransform()
    {
        if (shield_value <= 0)
        {
            if (shield.activeSelf)
                OnShieldBroken();
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

    public void LookAt(Vector3 pos)
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

                rb.velocity = transform.up * bigBallSpeed;
        }

        //InformGeter
        var ballItem = new ItemType(ob.transform, "大球", bp, rb);
        if (bp != null) bp.guid = ballItem.guid;
        InformGetter.AddItem(stage, ballItem);
    }

    public void ShotGun(HugeInt val,float angle = 0,int defaultNum = 0,int minVal = 0,int maxVal = 0)
    {
        ScreenShake.Instance?.ShortGunShake(val);
        if (angle == 0) angle = config.ShotGunAngle;
        if (defaultNum == 0) defaultNum = config.ShotGunBulletNum;
        if (minVal == 0) minVal = config.ShotGunMinVal;
        if (maxVal == 0) maxVal = config.ShotGunMaxVal;

        if (val / minVal < defaultNum) defaultNum = (int)(val / minVal).ToLong();
        else if (val / defaultNum > maxVal) defaultNum = (int)(val / maxVal).ToLong();
        if (defaultNum == 0) defaultNum = 1;
        int bv = (int)(val / defaultNum).ToLong();
        float da = angle / (defaultNum + 1);
        float sa = angle * -0.5f;
        for (int i = 1; i <= defaultNum; i++)
        {
            var dir = Quaternion.AngleAxis(sa + da * i, Vector3.back) * transform.up;

            float displayRadius = -1f;
            float impactScale = 1f;
            if (turretUpgraded > 0)
            {
                displayRadius = (BulletManager.Instance != null ? BulletManager.Instance.bulletDisplayRadius : 2f) * CurrentBulletRadiusScale;
                impactScale = CurrentBulletImpactScale;
            }
            BulletManager.Instance.Fire(transform.position, dir, stage, bv, config != null ? config.ShotGunBulletSpeed : bulletSpeed, displayRadius, impactScale);
        }
    }
}
