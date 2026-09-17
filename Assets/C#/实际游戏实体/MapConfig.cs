using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public enum WeaponKind
{
    霰弹,
    扫射,
    护盾,
    大球,
    任意,
    穿甲
}

[System.Serializable]
public class PropEntry
{
    // 运行时唯一指纹：武器栏 UI 的显示实例跟着这个 id 走（而不是槽位换皮），
    // 使用道具后剩余道具的图标才能用位移动画平滑前移。
    private static int nextId = Environment.TickCount & 0x7FFFFFFF;
    public int id = nextId++;

    public WeaponKind item;
    public HugeInt value;
    public int stage;
}

public class PropInfo
{
    public int stage;
    public WeaponKind kind;
    public HugeInt value;
    public ItemType aim;

    public PropInfo(WeaponKind kind, HugeInt value, int stage = 0, ItemType aim = null)
    {
        this.stage = stage;
        this.kind = kind;
        this.value = value;
        this.aim = aim;
    }
}


[System.Serializable]
public class ColorText
{
    public Color main = Color.white;
    public Color text = Color.white;
}

public class MapConfig : MonoBehaviour
{
    public static MapConfig Instance { get; private set; }

    [Header("AI")]
    public bool useAIDecision = false;

    [Header("Map")]
    public float worldSize = 10f;
    public int resolution = 1024;
    public int paintCost = 1; // 每像素占领消耗，不区分中立/敌方
    public float marbleSpeed = 10f; // 弹珠发射速度

    [Header("Colors")]
    public List<ColorText> teamColors = new()
    {
        new ColorText { main = new Color(0.5f, 0.5f, 0.5f), text = Color.white },
        new ColorText { main = Color.red, text = Color.white },
        new ColorText { main = Color.blue, text = Color.white },
        new ColorText { main = Color.green, text = Color.black },
        new ColorText { main = Color.yellow, text = Color.black }
    };

    [Header("ColorTimes")]
    public float TowelTimes = 0.1f;
    public float BallTimes = 0.1f;
    public float BulletTimes = 0.2f;
    public float DarkTimes = -0.1f;
    public float BrightTimes = 0.3f;

    [Header("Physics")]
    [Tooltip("子弹并入大球的动量传递系数：1=完全非弹性碰撞（同队按 M+m，敌队按 M-m）")]
    public float BulletImpactForce = 1f;
    [Header("ShotGun")]
    public float ShotGunAngle = 30f;
    public int ShotGunBulletNum = 512;
    public int ShotGunMinVal = 8;
    public int ShotGunMaxVal = 1073741824;
    public float ShotGunBulletSpeed = 4f; // 霰弹子弹速度
    public float NormalBulletSpeed = 4f; // 普通扫射/炮塔自动射击子弹速度
    [Header("穿甲弹")]
    [Tooltip("穿甲弹预制体：物理弹体（Rigidbody2D + CircleCollider2D + 穿甲弹贴图 + Trail + 数值文本）")]
    public GameObject pierceShellPrefab;
    [Tooltip("出膛速度（世界单位/秒）")]
    public float PierceShellSpeed = 8f;
    [Tooltip("穿盾时每秒扣护盾当前值的比例")]
    public float PierceShieldDrainPercentPerSecond = 0.25f;
    [Range(0.05f, 1f)]
    [Tooltip("在敌方护盾内时的速度倍率（照 LifeGame：穿甲弹在盾内被拖慢，但不改方向）")]
    public float PierceShieldSlowFactor = 0.5f;
    [Tooltip("离开护盾时的随机偏转角（±度）")]
    public float PierceExitDeflectAngle = 30f;

    [Header("Towel")]
    public int TowelDefaultBullets = 4096;
    public GameObject basicBallPrefab;
    [Header("Bounce")]
    public float bounceRate = 0.5f;

    public enum ColorStage { Default, Towel, Ball, Bullet,Dark , Bright }

    public Color GetColor(int stage, ColorStage kind = ColorStage.Default)
    {
        if (stage < 0 || stage >= teamColors.Count) return Color.black;
        Color c = teamColors[stage].main;
        switch (kind)
        {
            case ColorStage.Towel: c += TowelTimes * Color.white; break;
            case ColorStage.Ball: c += BallTimes * Color.white; break;
            case ColorStage.Bullet: c += BulletTimes * Color.white; break;
            case ColorStage.Dark: c += DarkTimes * Color.white; break;
            case ColorStage.Bright: c += BrightTimes * Color.white; break;
        }
        c.a = 1;
        return c;
    }

    /// <summary>获取指定阵营的文字颜色。</summary>
    public Color GetText(int stage)
    {
        if (stage < 0 || stage >= teamColors.Count) return Color.white;
        return teamColors[stage].text;
    }



    [Header("升级 / 能量（空槽升级与移动、悄悄话共用同一个池子）")]
    [Tooltip("每个空着的已解锁道具槽，每秒积累的升级值")]
    public float upgradePerEmptySlotPerSecond = 1f;
    [Tooltip("升级值达到该数值后触发一次升级三选一")]
    public float upgradeCost = 2f;
    [Tooltip("每次升级后，下一次升级所需值乘以这个倍率")]
    public float upgradeCostGrowth = 2.4f;
    [Tooltip("炮塔移动一次消耗的升级能量（这是**第一次**移动的价格）")]
    public float moveEnergyCost = 25f;
    [Tooltip("发一次悄悄话（秘密会晤）消耗的升级能量（这是**第一次**的价格）")]
    public float whisperEnergyCost = 25f;
    [Tooltip("涨价倍率：同一个动作每用过一次，下一次的价格 ×这个值（1 = 不涨价）。\n" +
             "移动、悄悄话各自单独累计，互不影响。\n" +
             "公开发言**不**花点数（每回合都要说的话，扣了就只能跳过，视频里就没台词了）")]
    public float actionCostGrowth = 1.5f;
    [Tooltip("悄悄话的冷却轮数：每隔这么多回合才能再发一次")]
    public int whisperCooldownRounds = 4;

    [Header("炮塔移动（原来是 Towel 上的字段，现在全队共用这一份配置）")]
    [Tooltip("移动速度（世界单位/秒）。走满最大移动距离的时间 = 最大距离 / 这个值")]
    public float moveSpeed = 0.25f;
    [Tooltip("每级炮塔强化叠加的移动速度（世界单位/秒）：等级越高走得越快，走满最大距离的时间跟着变短")]
    public float moveSpeedPerLevel = 0.05f;
    [Tooltip("0 级炮塔的最大移动距离（世界单位）")]
    public float moveRangeBase = 2f;
    [Tooltip("每级炮塔强化叠加的最大移动距离")]
    public float moveRangePerLevel = 0.5f;
    [Tooltip("移动预览的视野截图半径倍率：半径 = 这个值 × 当前最大移动距离。1 = 整个最大移动范围都在图里")]
    [Range(0.2f, 2f)] public float moveSightRadiusFactor = 1f;

    [Header("上一局回顾（写进各 AI 的系统提示词，让它记得上一局自己经历了什么）")]
    [Tooltip("下标 0~3 依次对应 1~4 号阵营，每个元素是该角色**第一人称视角**的上一局回顾；留空=这个阵营没参加上一局")]
    [TextArea(4, 12)]
    public List<string> lastGameRecap = new List<string>();

    [Header("Props")]
    public int propLimit = 0;
    public List<PropEntry>[] teamProps;

    //监听道具入栈出栈
    public static event Action<PropInfo> OnPropIn;
    public static event Action<PropInfo> OnPropOut;

    void Awake()
    {
        Instance = this;
        int count = teamColors.Count;
        teamProps = new List<PropEntry>[count];
        for (int i = 0; i < count; i++)
            teamProps[i] = new List<PropEntry>();
    }

    public void AddProp(int stage, WeaponKind item, HugeInt value)
    {
        if (stage < 0 || stage >= teamProps.Length) return;

        teamProps[stage].Add(new PropEntry { item = item, value = value, stage = stage });
        {
            OnPropIn?.Invoke(new PropInfo(item, value, stage));
        }
        // 超出上限，自动使用最新存入的道具（栈顶）
        while (teamProps[stage].Count > propLimit || (!useAIDecision && teamProps[stage].Count > 0))
        {
            int last = teamProps[stage].Count - 1;
            var top = teamProps[stage][last];
            teamProps[stage].RemoveAt(last);
            ExecutePropEffect(top.stage, top.item, top.value);//溢出（内部触发OnPropPop）
        }
    }
    /// <summary>
    /// 所有"实体武器"（不含【任意】本身）。**从枚举现算** —— 以后往 WeaponKind 里加武器，
    /// 【任意】的随机、【任意】的工具可选值、weapon 参数的文本解析都会自动带上，不用再手改列表。
    /// </summary>
    public static readonly WeaponKind[] AllConcreteWeapons = BuildConcreteWeapons();

    private static WeaponKind[] BuildConcreteWeapons()
    {
        List<WeaponKind> list = new List<WeaponKind>();
        foreach (WeaponKind kind in (WeaponKind[])Enum.GetValues(typeof(WeaponKind)))
            if (kind != WeaponKind.任意) list.Add(kind);
        return list.ToArray();
    }

    /// <summary>随机一种实体武器：【任意】的实际触发、以及阵亡时释放【任意】都用它。</summary>
    public static WeaponKind RandomConcreteWeapon()
    {
        if (AllConcreteWeapons.Length == 0) return WeaponKind.大球;
        return AllConcreteWeapons[Random.Range(0, AllConcreteWeapons.Length)];
    }

    /// <summary>实体武器的名字列表（工具的 @enum 与提示文案用，跟着枚举走）。</summary>
    public static List<string> ConcreteWeaponNames()
    {
        List<string> names = new List<string>();
        foreach (WeaponKind kind in AllConcreteWeapons) names.Add(kind.ToString());
        return names;
    }

    /// <summary>
    /// 执行一个道具。anyChoice 只对【任意】生效（null = 随机一种实体武器）。
    /// 返回**实际执行的那个武器**：【任意】就是在这一步定下来的，调用方可以拿它去写文案
    /// （不然观众只看到一句「随机武器」，不知道它到底打了什么）。
    /// </summary>
    public WeaponKind ExecutePropEffect(int stage, WeaponKind itemName, HugeInt val, ItemType aim_pos = null, WeaponKind? anyChoice = null, float aimAngleError = 0f)//anyChoice 只对【任意】生效；null=原来的随机
    {
        if (!Towel.AllTowel.TryGetValue(stage, out var towel)) return itemName;

        if (aim_pos != null && aim_pos.item != null)
        {
            towel.LookAt(aim_pos.pos); //转向,炮塔默认会自动顺时针转向
            towel.aimController.ChangeAim(aim_pos);

            // AI 调用道具的初始瞄准误差：在转向后立刻随机偏转，发射方向按误差后的朝向执行。
            if (aimAngleError > 0f)
                towel.transform.localEulerAngles += new Vector3(0f, 0f, Random.Range(-aimAngleError, aimAngleError));
        }
        OnPropOut?.Invoke(new PropInfo(itemName, val, stage, aim_pos));

        WeaponKind effectKind = itemName;
        if (effectKind == WeaponKind.任意)
        {
            // AI 指定了具体武器就用指定的；否则随机一种实体武器（含穿甲，以及以后新增的武器）
            effectKind = anyChoice ?? RandomConcreteWeapon();
            if (effectKind == WeaponKind.任意)
                effectKind = RandomConcreteWeapon();
        }

        ExecuteWeaponEffect(effectKind, towel, val);
        return effectKind;
    }

    private static void ExecuteWeaponEffect(WeaponKind kind, Towel towel, HugeInt val)
    {
        switch (kind)
        {
            case WeaponKind.霰弹:
                towel.ShotGun(val);
                break;
            case WeaponKind.扫射:
                towel.value += val;
                break;
            case WeaponKind.护盾:
                towel.shield_value += val;
                break;
            case WeaponKind.大球:
                towel.SpawnBigBall(val);
                break;
            case WeaponKind.穿甲:
                towel.SpawnShell(val);
                break;
        }
    }
}
