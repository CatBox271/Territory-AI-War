using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ForceShake : BaseShake
{
    public CurveTransform value;
    private IStageValue stageValue;

    private void Awake()
    {
        TryGetComponent(out stageValue);
    }

    public float bounceT
    {
        get
        {
            if (stageValue != null)
            {
                return value.Evaluate(stageValue.value);
            }
            return 1;
        }
    }

    public void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.TryGetComponent(out ForceShake fs))
        {
            float t = bounceT / fs.bounceT;
            fs.ToShake((collision.collider.transform.position - transform.position), 0.1f * t, 0.1f * t);
        }
    }
}
