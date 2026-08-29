using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public class ScreenShake : MonoBehaviour
{
    public static ScreenShake Instance;
    public CurveTransform ShortGunIntensity;
    public class Shake
    {
        public float intensity = 0;
        public float damp = 0;//频率
        public float mid_time = 0;//回程
        public float finish_time = 0;//回程
        public bool finish = false;

        public Vector3 pos = new();
        public Quaternion rotate = new();
        public Shake(float intensity, float damp, float time, float mid)
        {
            this.intensity = intensity;
            this.finish_time = time;
            this.mid_time = mid * time;
            this.damp = damp;
        }

    }
    List<Shake> shakes = new();

    private Vector3 pos;

    private void Awake()
    {
        SetDefault();
        Instance = this;
    }
    /// <summary>
    /// 记录当前位置为初始位置
    /// </summary>
    public void SetDefault()
    {
        pos = transform.position;
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
            transform.position = pos + _pos;
        }
    }

    public void ShortGunShake(HugeInt h)
    {
        float b = ShortGunIntensity.Evaluate(h);
        ToShake(0.025f * b, 0.02f, 0.2f * b, 0.1f);
    }

    public void ToShake(float intensity = 0.01f,float damp = 0.02f,float time = 0.2f, float mid = 0.5f)
    {
        var s = new Shake(intensity, damp, time, mid);
        StartCoroutine(Shaking(s));
        shakes.Add(s);
    }

    public IEnumerator Shaking(Shake shake)
    {
        Vector3 _pos = new();
        Quaternion _rotate = new();
        float time = 0;
        float damp_time = shake.damp;
        while (time < shake.finish_time)
        {
            if (time < shake.mid_time)
            {
                if (damp_time >= shake.damp)
                {
                    _pos = (Vector3)Random.insideUnitCircle * shake.intensity * (1 + time / shake.mid_time);
                    damp_time -= shake.damp;
                }
            }
            else
            {
                if (damp_time >= shake.damp)
                {
                    _pos = (Vector3)Random.insideUnitCircle * shake.intensity * ((shake.finish_time - time) / (shake.finish_time - shake.mid_time));
                    damp_time -= shake.damp;
                }
            }
            shake.pos = Vector3.Lerp(shake.pos, _pos, damp_time / shake.damp);

            time += Time.deltaTime;
            damp_time += Time.deltaTime;
            yield return null;
        }
        shake.pos = new();
        shake.rotate = new();
        shake.finish = true;
    }
}
