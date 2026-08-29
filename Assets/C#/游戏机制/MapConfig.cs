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
    任意
}

[System.Serializable]
public class PropEntry
{
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

public class MapConfig : MonoBehaviour
{
    public static MapConfig Instance { get; private set; }

    [Header("Map")]
    public float worldSize = 10f;
    public int resolution = 1024;
    public int paintCost = 1; // 每像素占领消耗，不区分中立/敌方
    public float marbleSpeed = 10f; // 弹珠发射速度

    [Header("Colors")]
    public List<Color> teamColors = new()
    {
        new(0.5f, 0.5f, 0.5f), Color.red, Color.blue, Color.green, Color.yellow
    };

    [Header("ColorTimes")]
    public float TowelTimes = 0.1f;
    public float BallTimes = 0.1f;
    public float BulletTimes = 0.2f;
    public float DarkTimes = -0.1f;

    [Header("Physics")]
    public float BulletImpactForce = 0.1f;
    [Header("ShotGun")]
    public float ShotGunAngle = 30f;
    public int ShotGunBulletNum = 512;
    public int ShotGunMinVal = 8;
    public int ShotGunMaxVal = 1073741824;

    [Header("Towel")]
    public int TowelDefaultBullets = 4096;
    public GameObject basicBallPrefab;
    [Header("Bounce")]
    public float bounceRate = 0.5f;

    public enum ColorStage { Default, Towel, Ball, Bullet,Dark }

    public Color GetColor(int stage, ColorStage kind = ColorStage.Default)
    {
        if (stage < 0 || stage >= teamColors.Count) return Color.black;
        Color c = teamColors[stage];
        switch (kind)
        {
            case ColorStage.Towel: c += TowelTimes * Color.white; break;
            case ColorStage.Ball: c += BallTimes * Color.white; break;
            case ColorStage.Bullet: c += BulletTimes * Color.white; break;
            case ColorStage.Dark: c += DarkTimes * Color.white; break;
        }
        c.a = 1;
        return c;
    }

    [Header("AI")]
    public bool useAIDecision = false;

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
    public void ExecutePropEffect(int stage, WeaponKind itemName, HugeInt val, ItemType aim_pos = null, WeaponKind? anyChoice = null)//anyChoice 只对【任意】生效；null=原来的随机
    {
        if (!Towel.AllTowel.TryGetValue(stage, out var towel)) return;

        if (aim_pos != null && aim_pos.item != null)
        {
            towel.LookAt(aim_pos.pos); //转向,炮塔默认会自动顺时针转向
            towel.aimController.ChangeAim(aim_pos);
        }
        OnPropOut?.Invoke(new PropInfo(itemName, val, stage, aim_pos));

        WeaponKind effectKind = itemName;
        if (effectKind == WeaponKind.任意)
        {
            // AI 指定了具体武器就用指定的；否则维持原来的随机逻辑。
            effectKind = anyChoice ?? (WeaponKind)Random.Range(0, 4);
            if (effectKind == WeaponKind.任意)
                effectKind = (WeaponKind)Random.Range(0, 4);
        }

        ExecuteWeaponEffect(effectKind, towel, val);
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
        }
    }
}
