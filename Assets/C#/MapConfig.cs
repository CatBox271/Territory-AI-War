using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class PropEntry
{
    public string item;
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
    public List<PropEntry>[] teamProps;

    void Awake()
    {
        Instance = this;
        int count = teamColors.Count;
        teamProps = new List<PropEntry>[count];
        for (int i = 0; i < count; i++)
            teamProps[i] = new List<PropEntry>();
    }

    public void AddProp(int stage, string item, HugeInt value)
    {
        if (stage < 0 || stage >= teamProps.Length) return;
        teamProps[stage].Add(new PropEntry { item = item, value = value, stage = stage });
    }

    public void ExecutePropEffect(int stage, string itemName, HugeInt val)
    {
        if (!Towel.AllTowel.TryGetValue(stage, out var towel)) return;

        switch (itemName)
        {
            case "霰弹":
            case "散弹":
                towel.ShotGun(val);
                break;
            case "扫射":
                towel.value += val;
                break;
            case "护盾":
                towel.shield_value += val;
                break;
            case "大球":
                towel.SpawnBigBall(val);
                break;
            default: // 任意
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
