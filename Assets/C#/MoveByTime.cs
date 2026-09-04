using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveByTime : MonoBehaviour
{
    public float Xscale = 1;
    public float Yscale = 1;
    public AnimationCurve X;
    public AnimationCurve Y;
    private Vector3 pos;
    void Start()
    {
        pos = transform.position;
    }

    // Update is called once per frame

    int pass = 0;
    void Update()
    {
        pass++;
        if (pass < 600) return;
        pass = 0;
        float t = (Time.time / 60f);
        transform.position = pos + new Vector3(X.Evaluate(t) * Xscale, Y.Evaluate(t) *Yscale);
    }
}
