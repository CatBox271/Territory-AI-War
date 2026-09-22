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

    private const float Radius = 0.35f;
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

    [Header("地图炫光（MapGlow 组件挂在 prefab 上，这里只负责按阵营赋色）")]
    [Tooltip("prefab 上的 MapGlow；留空会自动找本物体上的第一个")]
    public MapGlow glow;
    [Tooltip("用自己阵营的亮色当炫光颜色（MapConfig.GetColor(stage, Bright)）；关掉就不动颜色，用 MapGlow 自己填的那个")]
    public bool glowUseTeamColor = true;

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
    /// <summary>每级炮塔强化让极限转速翻的倍数（升级选择卡片上的数值也读它，别只改一边）。</summary>
    public const float GuardSpeedPerLevel = 2f;
    /// <summary>每级炮塔升级让极限转速再翻一倍。</summary>
    public float CurrentGuardSpeedScale => Mathf.Pow(GuardSpeedPerLevel, Mathf.Max(0, turretUpgraded));

    #region 炮塔移动

    // 炮塔移动：参数统一放 MapConfig（全队共用一份，见 MapConfig「炮塔移动」那段），这里只做只读转发。
    // 名字故意保持不变：ReactionSystem / InformGetter / 升级卡片都在读 `towel.moveSpeed` 这类写法。
    // config 拿不到时（编辑器里单独打开预制体、或还没 Awake）回退到各自默认值，别出现除零。
    /// <summary>移动速度（世界单位/秒）：基础速度 + 每级炮塔强化叠加，等级越高走得越快。</summary>
    public float moveSpeed => config != null
        ? config.moveSpeed + config.moveSpeedPerLevel * Mathf.Max(0, turretUpgraded)
        : 0.25f;
    /// <summary>0 级时的最大移动距离（世界单位）；每级炮塔强化再叠加 moveRangePerLevel。</summary>
    public float moveRangeBase => config != null ? config.moveRangeBase : 2f;
    /// <summary>每级炮塔强化增加的最大移动距离。</summary>
    public float moveRangePerLevel => config != null ? config.moveRangePerLevel : 0.5f;
    /// <summary>每级炮塔强化增加的移动速度。</summary>
    public float moveSpeedPerLevel => config != null ? config.moveSpeedPerLevel : 0.05f;

    /// <summary>当前最大移动距离：基础值 + 每级炮塔强化叠加。</summary>
    public float MaxMoveDistance => moveRangeBase + moveRangePerLevel * Mathf.Max(0, turretUpgraded);

    /// <summary>当前最大移动时间：由最大移动距离和移动速度推出，不单独配置（走完上限距离刚好用完）。</summary>
    public float MaxMoveTime => MaxMoveDistance / Mathf.Max(moveSpeed, 0.0001f);

    /// <summary>可移动范围：炮塔中心坐标绝对值上限 = 地图半边 − 基地半径。</summary>
    public float MoveBound => config != null ? Mathf.Max(0f, config.worldSize * 0.5f - Radius) : 4f;

    private bool moving;
    private Vector2 moveTarget;
    private float moveStartTime;

    public bool IsMoving => moving;

    /// <summary>移动状态的人类可读写法（进 AI 情报）：剩余秒数按“到目标点的剩余距离/速度”算，即真实到达时间。</summary>
    public string MoveStateText
    {
        get
        {
            if (!moving) return "静止";
            float left = Vector2.Distance(transform.position, moveTarget) / Mathf.Max(moveSpeed, 0.0001f);
            return "正在向 (" + moveTarget.x.ToString("0.00") + ", " + moveTarget.y.ToString("0.00") + ") 移动，剩余 " + left.ToString("0.0") + " 秒";
        }
    }

    /// <summary>开始朝 dir 方向移动；距离按当前最大移动距离夹过（超出部分走不到）。distance=0 等于立即停下。</summary>
    public void StartMove(Vector2 dir, float distance)
    {
        if (isDead) return;
        if (dir.sqrMagnitude < 0.0000001f) { StopMove("方向无效，未移动"); return; }

        dir = dir.normalized;
        float d = Mathf.Clamp(distance, 0f, MaxMoveDistance);
        moveTarget = (Vector2)transform.position + dir * d;
        moveStartTime = Time.time;
        moving = true;
        // 对局数据：移动记录（起点 → 目标 + 计划距离）
        GameStats.NoteMoveStart(stage, transform.position, moveTarget, d);

        if (d <= 0f) StopMove("原地停下");
    }

    /// <summary>停止移动；reason 非空时记一条一次性提示给 AI。</summary>
    public void StopMove(string reason = null)
    {
        moving = false;
        // 对局数据：移动结束（落点 + 耗时 + 结束原因）
        GameStats.NoteMoveEnd(stage, transform.position, reason);
        if (!string.IsNullOrEmpty(reason)) InformGetter.NotifyTurretMoveDone(stage, reason);
    }

    /// <summary>每帧推进移动：朝目标走并夹在地图可移动范围内；到达/撞墙/超时都停下并通知 AI。</summary>
    private void UpdateMove()
    {
        if (!moving) return;

        Vector2 next = Vector2.MoveTowards(transform.position, moveTarget, moveSpeed * Time.deltaTime);
        float b = MoveBound;
        Vector2 clamped = new Vector2(Mathf.Clamp(next.x, -b, b), Mathf.Clamp(next.y, -b, b));
        bool hitWall = (clamped - next).sqrMagnitude > 0.0000001f;
        transform.position = new Vector3(clamped.x, clamped.y, transform.position.z);

        if ((clamped - moveTarget).sqrMagnitude <= 0.000001f) { StopMove("已抵达目标"); return; }
        if (hitWall) { StopMove("撞到地图边界，已停下"); return; }
        if (Time.time - moveStartTime >= MaxMoveTime) StopMove("移动超时中断");
    }

    #endregion

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

        // 塔身底下那圈炫光：组件挂在 prefab 上，这里只按阵营赋色（形状/半径/强度都在 prefab 的 MapGlow 上调）
        if (glow == null) glow = GetComponent<MapGlow>();
        if (glow != null && glowUseTeamColor)
            glow.SetColor(config.GetColor(stage, MapConfig.ColorStage.Bright));
        value = config.TowelDefaultBullets;
        PaintInitialCircle();
        LookAt(Random.insideUnitCircle / 100f);
        ShotGun(1048576, 60, 1024);

        Say("HelloWorld");

        //InformGeter初始化：只登记开局位置（之后不再更新）
        //炮塔与护盾不再进全局场上信息：敌方炮塔会移动，其位置只能靠撞击情报反推。
        InformGetter.RegisterInitialPosition(stage, transform.position);
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
        UpdateMove();
    }

    /// <summary>
    /// 炮塔上的逐字飘字。【暂时关闭】要恢复显示，把下面这行改回 `messageDisplayer.Say(content, force)` 即可。
    /// 只关这一条显示通道：工具操作、中央发言列表、ShowTip（升级提示走 tipPrefab）都不经过这里，不受影响。
    /// </summary>
    public bool Say(string content, bool force = false) => false;   // 暂时取消炮塔上的 Say 显示

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

    /// <summary>
    /// 第 level 级护盾强化的**破盾无敌时长**（秒）：**每次升级累加 `shieldBreakInvincibleTime / 2^N`，N 从 0 开始**
    /// （第 1 次升级 N=0 → 加满一份基准、第 2 次 N=1 → 加半份、第 3 次 N=2 → 加四分之一份…）。
    /// 于是总和 = `基准 × (2 − 2^(1−level))`：1 级 基准、2 级 1.5×基准、3 级 1.75×基准…
    /// **上限趋近 2×基准**（基准在 Towel.prefab 上是 10 → 10 / 15 / 17.5 / 18.75…趋近 20 秒）。
    /// （2026-09-22 用户："把护盾无敌改为+=10/(2的N次升级)" + "N初始值为0！"；
    ///  这条取代了更早的 `基准 / 4^(level-1)` 口径。）
    /// level &lt;= 0 = 没升过护盾强化，没有无敌窗口。
    /// </summary>
    public float ShieldInvincibleTimeAt(int level)
    {
        if (level <= 0) return 0f;
        return shieldBreakInvincibleTime * (2f - Mathf.Pow(2f, 1f - level));
    }

    /// <summary>护盾被击碎（或检测到已碎）时调用，立即开启无敌窗口与发光。</summary>
    public void OnShieldBroken()
    {
        if (shield != null && shield.activeSelf) shield.SetActive(false);
        if (shieldUpgradeOwned <= 0) return;

        invincibleUntil = Time.time + ShieldInvincibleTimeAt(shieldUpgradeOwned);
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
        // 死亡瞬间先处理弹珠：只记自己阵营的数值，并立刻把自己阵营的弹珠全删掉（别人的弹珠不动）。
        // 记下来的数值等遗言说完，再一颗颗放成大球。倒序遍历。
        List<uint> myMarbleExponents = new List<uint>();
        Marble[] marbles = FindObjectsOfType<Marble>();
        for (int i = marbles.Length - 1; i >= 0; i--)
        {
            Marble marble = marbles[i];
            if (marble == null || marble.stage != stage) continue;
            if (marble.ValueExponent > 0) myMarbleExponents.Add(marble.ValueExponent);
            Destroy(marble.gameObject);
        }

        // 遗言放在死亡流程最前面：先说完遗言，再开始释放大球
        var lastWords = AIAgent.OnStageDeathAsync(stage, killerStage, killerWeapon);
        while (!lastWords.IsCompleted) yield return null;
        yield return new WaitForSeconds(1.5f); // 遗言气泡至少显示一会儿，避免立即销毁导致看不到

        foreach (uint exponent in myMarbleExponents)
        {
            SpawnBigBall(HugeInt.Pow(2, (int)exponent));
            yield return new WaitForSeconds(0.2f);
        }

        // 死亡时把道具栈里的道具按"道具本体"释放出去（护盾/扫射放不出去，见 ReleasePropAsWeapon）
        if (config != null && config.teamProps != null && stage >= 0 && stage < config.teamProps.Length)
        {
            List<PropEntry> props = config.teamProps[stage];
            if (props != null)
            {
                foreach (PropEntry prop in new List<PropEntry>(props))
                {
                    ReleasePropAsWeapon(prop);
                    yield return new WaitForSeconds(0.2f);
                }
                props.Clear();
            }
        }

        // 炮塔上带的两样：子弹量 → 一颗大球
        if (value > 0)
        {
            SpawnBigBall(value);
            yield return new WaitForSeconds(0.2f);
        }

        // 护盾值 → 一颗大球
        if (shield_value > 0)
        {
            SpawnBigBall(shield_value);
            yield return new WaitForSeconds(0.2f);
        }

        // 死亡释放**只有这四样**：弹珠、道具、子弹量、护盾值。
        // 其它一律不管：还在飞的子弹、大球、穿甲弹都不进遗产，照旧留在场上。

        if (MarbleManager.Instance != null)
            MarbleManager.Instance.OnTeamDeath(stage);

        AllTowel.Remove(stage);
        Destroy(gameObject);
    }

    /// <summary>
    /// 阵亡时把一个道具"放出去"：**按道具本体释放** —— 大球放球、穿甲放穿甲弹、霰弹放霰弹；
    /// 护盾与扫射本身是给自己的增益、放不出去，统一变成大球。
    /// 【任意】先按正常使用那样随机开一种武器，再走同一套释放映射。
    /// 方向沿用炮塔当时的朝向。
    /// </summary>
    void ReleasePropAsWeapon(PropEntry prop)
    {
        if (prop == null) return;

        WeaponKind kind = prop.item;
        if (kind == WeaponKind.任意)
        {
            // 和正常使用【任意】同一个随机口径：所有实体武器（跟着 WeaponKind 枚举走，新增武器自动进池）
            kind = MapConfig.RandomConcreteWeapon();
        }
        ReleaseWeaponOnDeath(kind, prop.value);
    }

    /// <summary>死亡释放的映射：穿甲放穿甲弹、霰弹放霰弹，其余（大球/护盾/扫射）一律放大球。</summary>
    void ReleaseWeaponOnDeath(WeaponKind kind, HugeInt value)
    {
        switch (kind)
        {
            case WeaponKind.穿甲:
                SpawnShell(value);
                break;
            case WeaponKind.霰弹:
                ShotGun(value);
                break;
            default:   // 大球 / 护盾 / 扫射
                SpawnBigBall(value);
                break;
        }
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

    /// <summary>
    /// 子弹出膛位置。MapConfig.bulletSpawnRingRadius &lt;= 0 时**严格从塔的原点出膛**（连 BulletPosRandom 抖动都不加，
    /// 完全等于没加这个功能之前）；半径 &gt; 0 时沿瞄准方向从塔心这个半径处出膛（再叠环带厚度随机 + BulletPosRandom 抖动）。
    /// 每次读到的半径值一变就在 Console 打一行（不刷屏），方便直接看代码到底读到了几。
    /// </summary>
    Vector2 BulletSpawnPos(Vector2 dir)
    {
        Vector2 center = transform.position;
        float radius = config != null ? config.bulletSpawnRingRadius : 0f;

        if (radius <= 0f)
        {
            LogSpawnRingOnce(radius, center);
            return center;
        }

        float thickness = config != null ? config.bulletSpawnRingThickness : 0f;
        float distance = radius + (thickness > 0f ? Random.Range(-0.5f, 0.5f) * thickness : 0f);
        Vector2 outward = dir.sqrMagnitude > 0.0001f ? dir.normalized : (Vector2)transform.up;
        Vector2 pos = center + outward * distance + Random.insideUnitCircle * BulletPosRandom;

        LogSpawnRingOnce(radius, pos);
        return pos;
    }

    // 只在"读到的半径"变化时打一行，不刷屏；用来确认 MapConfig 里那个值到底有没有被代码读到。
    private static float loggedSpawnRingRadius = float.NaN;

    private void LogSpawnRingOnce(float radius, Vector2 spawnPos)
    {
        if (!float.IsNaN(loggedSpawnRingRadius) && Mathf.Approximately(loggedSpawnRingRadius, radius)) return;
        loggedSpawnRingRadius = radius;
        Debug.Log($"[子弹出膛] {name} 代码读到的出膛环半径 = {radius}；塔原点 = {transform.position}；这次出膛点 = {spawnPos}；" +
                  $"MapConfig 实例 {config.GetInstanceID()}" +
                  (MapConfig.Instance != null ? $"（Instance {MapConfig.Instance.GetInstanceID()}）" : "（Instance 为空）"));
    }

    void FireWithDir(Vector2 dir)
    {
        if (value <= 0) return;
        int bv = (int)bulletCount.Evaluate(value);
        if (bv > value) bv = (int)value.ToLong();
        value -= bv;
        float maxAngle = bulletRandomSpeed.Evaluate(value);
        var finalDir = (Vector2)(Quaternion.AngleAxis(Random.Range(-maxAngle, maxAngle), Vector3.forward) * dir);
        var pos = BulletSpawnPos(finalDir);

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
    /// <summary>
    /// 穿甲弹：实例化配置里的预制体，把 **MapConfig 上的穿甲口径**写进弹体，然后出膛。
    /// 口径统一放在 MapConfig（预制体上只留贴图/Trail/SpeedCurve 这类固定参数），
    /// 所以这里必须把 shield 交互与出膛速度刷进去，否则弹体就用预制体上的占位值。
    /// </summary>
    public void SpawnShell(HugeInt val)
    {
        if (config.pierceShellPrefab == null) return;
        var ob = Instantiate(config.pierceShellPrefab, transform.position, Quaternion.identity);
        var se = ob.GetComponent<StageEditor>();
        if (se != null) { se.enabled = false; Destroy(se); }
        var bp = ob.GetComponent<BallPainter>();
        if (bp != null)
        {
            bp.stage = stage;
            bp.value = val;
            // 子弹不能弹飞穿甲：口径同样放在 MapConfig（见 PierceShellBulletImpactFactor），发射时刷进弹体
            bp.bulletImpactFactor = config.PierceShellBulletImpactFactor;
        }

        // 穿盾口径：MapConfig → 弹体上的 ShieldSlow
        var shell = ob.GetComponent<ShieldSlow>();
        if (shell != null)
        {
            // 盾内速度倍率：ShieldSlow 会写 valueEditor.value（= BallPainter.SpeedTimes）× 这个值
            shell.SlowTimes = config.PierceShieldSlowFactor;
            // 每秒啃掉的护盾比例
            shell.CostSpeed = config.PierceShieldDrainPercentPerSecond;
            // 离开护盾时随机偏转 ±N 度（角度 <= 0 就当关掉）
            shell.OutRandom = config.PierceExitDeflectAngle > 0f;
            shell.RandomAngle = config.PierceExitDeflectAngle;
        }

        var rb = ob.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            // 穿甲速度口径：MapConfig 里的 PierceShellSpeed **直接写进弹体的 SpeedTimes**
            // （弹体的持续速度 = SpeedCurve × SpeedTimes，这里不碰曲线）。
            if (bp != null) bp.SpeedTimes = config.PierceShellSpeed;
            rb.velocity = transform.up * config.PierceShellSpeed;
        }

        //InformGeter
        // 名字用弹体自己声明的 game_item_name（穿甲.prefab 上写的是"穿甲"），别再硬编码"大球"
        var ballItem = new ItemType(ob.transform, bp != null ? bp.game_item_name : "穿甲", bp, rb);
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
            BulletManager.Instance.Fire(BulletSpawnPos(dir), (Vector2)dir, stage, bv, config != null ? config.ShotGunBulletSpeed : bulletSpeed, displayRadius, impactScale);
        }
    }
}
