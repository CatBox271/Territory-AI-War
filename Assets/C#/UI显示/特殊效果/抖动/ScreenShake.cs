using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public class ScreenShake : BaseShake
{
    public static ScreenShake Instance;
    public CurveTransform ShortGunIntensity;

    private void Awake()
    {
        // ScreenShake 没有外部 shakingAim，摇的是自己：让基类把偏移应用到自己身上
        shakingAim = transform;
        SetDefault();
        Instance = this;
    }

    public void ShortGunShake(HugeInt h)
    {
        float b = ShortGunIntensity.Evaluate(h);
        ToShake(0.025f * b, 0.02f, 0.2f * b, 0.1f);
    }

    public void ToShake(float intensity = 0.01f, float damp = 0.02f, float time = 0.2f, float mid = 0.5f)
    {
        // force 传 0，基类会走 Random.insideUnitCircle 随机抖动
        base.ToShake(Vector3.zero, intensity, time, damp, mid);
    }
}
