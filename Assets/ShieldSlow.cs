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
        origin_speed = valueEditor.value;
        for (int i = 1; i < 5; i++)
        {
            if (i == stageValue.stage) continue;
            if (!Towel.AllTowel.TryGetValue(i, out Towel t)) continue;
            Transform tf = t.transform.GetChild(0);
            shields[i] = new sv(tf.GetComponent<IStageValue>(), tf);
        }
    }
    private void FixedUpdate()
    {
        for (int i = 1; i < 5; i++)
        {
            if (!shields.TryGetValue(i, out sv shield)) continue;
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
