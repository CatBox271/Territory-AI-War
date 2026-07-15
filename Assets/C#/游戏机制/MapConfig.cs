using System.Collections.Generic;
using UnityEngine;

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

public class MapConfig : MonoBehaviour
{
    public static MapConfig Instance { get; private set; }

    [Header("Map")]
    public float worldSize = 10f;
    public int resolution = 1024;

    [Header("Colors")]
    public List<Color> teamColors = new()
    {
        new(0.5f, 0.5f, 0.5f), Color.red, Color.blue, Color.green, Color.yellow
    };

    [Header("ColorTimes")]
    public float TowelTimes = 0.1f;
    public float BallTimes = 0.1f;
    public float BulletTimes = 0.2f;

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

    public enum ColorStage { Default, Towel, Ball, Bullet }

    public Color GetColor(int stage, ColorStage kind = ColorStage.Default)
    {
        if (stage < 0 || stage >= teamColors.Count) return Color.black;
        Color c = teamColors[stage];
        switch (kind)
        {
            case ColorStage.Towel: c += TowelTimes * Color.white; break;
            case ColorStage.Ball: c += BallTimes * Color.white; break;
            case ColorStage.Bullet: c += BulletTimes * Color.white; break;
        }
        c.a = 1;
        return c;
    }

    [Header("AI")]
    public bool useAIDecision = false;

    [Header("Props")]
    public int propLimit = 5;
    public List<PropEntry>[] teamProps;

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

        // 超出上限，自动使用最新存入的道具（栈顶）
        while (teamProps[stage].Count >= propLimit)
        {
            int last = teamProps[stage].Count - 1;
            var top = teamProps[stage][last];
            teamProps[stage].RemoveAt(last);
            ExecutePropEffect(top.stage, top.item, top.value);//溢出
        }

        teamProps[stage].Add(new PropEntry { item = item, value = value, stage = stage });
    }

    public void ExecutePropEffect(int stage, WeaponKind itemName, HugeInt val, ItemType aim_pos = null)//这里需要添加ItemType作为目标。
    {
        if (!Towel.AllTowel.TryGetValue(stage, out var towel)) return;

        if (aim_pos != null && aim_pos.item != null) towel.LookAt(aim_pos.pos); //转向,炮塔默认会自动顺时针转向

        switch (itemName)
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
            case WeaponKind.任意:
                switch (Random.Range(0, 4))
                {
                    case 0: towel.ShotGun(val); break;
                    case 1: towel.value += val; break;
                    case 2: towel.shield_value += val; break;
                    case 3: towel.SpawnBigBall(val); break;
                }
                break;
        }
    }
}
