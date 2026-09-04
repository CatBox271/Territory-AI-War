using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EffectManager : MonoBehaviour
{
    public static EffectManager Instance;
    public GameObject CE;

    private void Awake()
    {
        Instance = this;
    }

    public void Boom(Vector3 pos,Color col, int count = 15,float speed = 5f,float startSize = 0.5f,float DieSpeed = 1)
    {
        for (int i = 0; i < count; i++)
        {
            GameObject go = Instantiate(CE, transform.parent);
            go.transform.position = pos;
            SlowSmall ss = go.GetComponent<SlowSmall>();
            ss.SetVelocity(Random.insideUnitCircle.normalized * speed, col,startSize,DieSpeed);
        }
    }
}
