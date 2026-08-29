using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BaseShake : MonoBehaviour
{
    public Transform shakingAim;

    private void Awake()
    {
        SetDefault();
    }
    public class Shake
    {
        public float intensity = 0;
        public float damp = 0;//频率
        public float mid_time = 0;//回程
        public float finish_time = 0;//回程
        public Vector2 force = new(0, 1);//运动方向//0，0是随机
        public bool finish = false;

        public Vector3 pos = new();
        public Shake(Vector3 force, float intensity, float damp, float time, float mid)
        {
            this.intensity = intensity;
            this.finish_time = time;
            this.mid_time = mid * time;
            this.damp = damp;
            this.force = ((Vector2)force == Vector2.zero) ? force : force.normalized;
        }

    }
    List<Shake> shakes = new();

    private Vector3 pos;

    /// <summary>
    /// 记录当前位置为初始位置
    /// </summary>
    public void SetDefault()
    {
        if (shakingAim != null) pos = shakingAim.localPosition;
    }
    private void Update()
    {
        if (shakes.Count > 0)
        {
            Vector3 _pos = new();
            for (int i = shakes.Count - 1; i > -1; i--)
            {
                Shake shake = shakes[i];
                if (shake.finish)
                {
                    shakes.RemoveAt(i);
                    continue;
                }
                _pos += shake.pos;
            }
            if (shakingAim != null) shakingAim.localPosition = pos + _pos;
        }
    }

    public void ToShake(Vector3 force, float intensity = 0.1f, float time = 0.1f, float damp = 0.033f, float mid = 0.1f)
    {
        var s = new Shake(force, intensity, damp, time, mid);
        StartCoroutine(Shaking(s));
        shakes.Add(s);
    }

    public IEnumerator Shaking(Shake shake)
    {
        Vector3 _pos = new();
        float time = 0;
        float damp_time = shake.damp;
        bool tt = true;
        while (time < shake.finish_time)
        {
            Vector3 f = (Vector3)(shake.force != Vector2.zero ? shake.force * (tt ? 1 : -1) : Random.insideUnitCircle) * shake.intensity;
            if (time < shake.mid_time)
            {
                if (damp_time >= shake.damp)
                {

                    _pos = f * (1 + time / shake.mid_time);
                    damp_time -= shake.damp;
                    tt = !tt;
                }
            }
            else
            {
                if (damp_time >= shake.damp)
                {
                    _pos = f * ((shake.finish_time - time) / (shake.finish_time - shake.mid_time));
                    damp_time -= shake.damp;
                    tt = !tt;
                }
            }
            shake.pos = Vector3.Lerp(shake.pos, _pos, damp_time / shake.damp);

            time += Time.deltaTime;
            damp_time += Time.deltaTime;
            yield return null;
        }
        shake.pos = new();
        shake.finish = true;
    }
}
