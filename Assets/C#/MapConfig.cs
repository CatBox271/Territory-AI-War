using System.Collections.Generic;
using UnityEngine;

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

    void Awake() { Instance = this; }
}
