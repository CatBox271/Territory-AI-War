using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ShieldSlow : MonoBehaviour
{
    public float SlowTimes = 1f;
    public float CostSpeed = 0.25f;
    public float EffectIntensity;
    public bool OutRandom = false;
    /// <summary>离开护盾时的随机偏转角（±度，发射时由 MapConfig.PierceExitDeflectAngle 写进来）</summary>
    public float RandomAngle = 30f;
    public Rigidbody2D rb;

    private Dictionary<int, sv> shields = new();
    private IValueEditor valueEditor;
    private IStageValue stageValue;
    private IStringGetter guid;
    private float origin_speed = 0;
    private class sv
    {
        public IStageValue value;
        public Transform tf;
        public bool inside = false;
        public sv(IStageValue value,Transform tf)
        {
            this.value = value;
            this.tf = tf;
        }
    }


    private void Start()
    {
        valueEditor = GetComponent<IValueEditor>();
        stageValue = GetComponent<IStageValue>();
        guid = GetComponent<IStringGetter>();
        if (rb == null) rb = GetComponent<Rigidbody2D>();
        // 基准取弹体自己的 SpeedTimes（穿甲弹上就是 Towel 写进来的 MapConfig 口径）。
        // 不能拿"巡航速度"来当基准：巡航速度 = SpeedCurve × SpeedTimes，把它回写进 SpeedTimes
        // 等于又乘了一次曲线值，越进出盾越偏。
        BallPainter painter = GetComponent<BallPainter>();
        origin_speed = painter != null ? painter.SpeedTimes : valueEditor.value;
        for (int i = 1; i < 5; i++)
        {
            if (i == stageValue.stage) continue;
            Transform tf = FindShieldTransform(i); // 统一走带校验的查找，炮塔缺失时不留脏引用
            if (tf == null) continue;
            IStageValue svValue = tf.GetComponent<IStageValue>();
            if (svValue == null) continue;
            shields[i] = new sv(svValue, tf);
        }
    }
    /// <summary>
    /// shields 里缓存的是各阵营炮塔子物体的 Transform，而炮塔被淘汰时会 Destroy 掉，
    /// 缓存对象会变成"已销毁"（MissingReferenceException）。所以每次取用前都重新验证：
    /// 已成 null 就查一遍 Towel.AllTowel，取不到就从字典里删掉。
    /// </summary>
    private bool TryGetShield(int stage, out sv shield)
    {
        shield = null;
        if (!shields.TryGetValue(stage, out sv cached)) return false;

        if (cached.tf == null) // Unity 的 == 能正确识别"已销毁"
        {
            cached.tf = FindShieldTransform(stage);
            if (cached.tf == null)
            {
                shields.Remove(stage);
                return false;
            }
            cached.value = cached.tf.GetComponent<IStageValue>();
            if (cached.value == null)
            {
                shields.Remove(stage);
                return false;
            }
        }
        shield = cached;
        return true;
    }

    /// <summary>按阵营号取炮塔子物体的 Transform；查不到或没子物体时返回 null。</summary>
    private static Transform FindShieldTransform(int stage)
    {
        if (!Towel.AllTowel.TryGetValue(stage, out Towel t) || t == null) return null;
        if (t.transform.childCount <= 0) return null;
        return t.transform.GetChild(0);
    }

    private void FixedUpdate()
    {
        for (int i = 1; i < 5; i++)
        {
            if (!TryGetShield(i, out sv shield)) continue;
            if (Vector3.Distance(transform.position, shield.tf.position) <= shield.tf.lossyScale.x)
            {
                Cost(shield.value);
                if (!shield.inside)
                {
                    valueEditor.value = origin_speed * SlowTimes;
                    shield.inside = true;
                }
            }
            else if (shield.inside)
            {
                shield.inside = false;
                valueEditor.value = origin_speed;
                if (OutRandom) DeflectOnExit();
            }
        }
    }
    void Cost(IStageValue shield)
    {

        HugeInt hit_value = shield.value.Multiply(CostSpeed * Time.fixedDeltaTime);
        if (hit_value > stageValue.value) hit_value = stageValue.value;
        stageValue.Hit(shield.stage, shield.Hit(stageValue.stage, hit_value, guid?.stringInfo, $"{stageValue.stage}号阵营的穿甲"), "", $"{shield.stage}号阵营的护盾");
    }

    /// <summary>离开护盾：只转方向、不改速度大小，随机偏转 ±RandomAngle 度。</summary>
    void DeflectOnExit()
    {
        if (rb == null) return;
        Vector2 v = rb.velocity;
        if (v.sqrMagnitude < 0.000001f) return;
        rb.velocity = Quaternion.AngleAxis(Random.Range(-RandomAngle, RandomAngle), Vector3.forward) * v;
    }
}
