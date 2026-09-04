using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class upgrade_show : MonoBehaviour
{
    public SpriteRenderer sp;
    public float start_time;
    public float speed;
    public AnimationCurve alpha;
    public AnimationCurve y;
    private Vector3 pos;
    void Start()
    {
        StartCoroutine(show());   
    }
    IEnumerator show()
    {
        pos = transform.position;
        Color a = sp.color;a.a = 0;
        sp.color = a;
        yield return new WaitForSeconds(start_time);
        float e = 0;
        while (true)
        {
            e = (Time.time - start_time) * speed;
            a.a = alpha.Evaluate(e);
            transform.position = new Vector3(0, y.Evaluate(e), 0) + pos;
            sp.color = a;
            yield return null;
        }
    }
}
