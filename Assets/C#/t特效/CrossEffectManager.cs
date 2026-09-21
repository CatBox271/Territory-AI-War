using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CrossEffectManager : MonoBehaviour
{
    public static CrossEffectManager Instance;
    public Queue<SlowSmall> inacive = new();
    public GameObject CE;
    private void Awake()
    {
        Instance = this;
        AddItem(64);
    }

    public void Boom(Vector3 pos, Color col, int count = 15, float speed = 5f, float startSize = 0.5f, float DieSpeed = 1, Vector2 baseDir = default, float spreadDeg = 360f)
    {
        for (int i = 0; i < count; i++)
        {
            var ss = GetItem();
            var go = ss.gameObject;
            go.SetActive(true);
            go.transform.position = pos;

            Vector2 dir;
            if (baseDir.sqrMagnitude > 0.0001f && spreadDeg < 360f)
            {
                float baseAngle = Mathf.Atan2(baseDir.y, baseDir.x) * Mathf.Rad2Deg;
                float angle = (baseAngle + Random.Range(-spreadDeg, spreadDeg)) * Mathf.Deg2Rad;
                dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            }
            else
            {
                dir = Random.insideUnitCircle.normalized;
            }
            ss.SetVelocity(dir * speed, col, startSize, DieSpeed);
        }
    }

    private SlowSmall GetItem()
    {
        if (inacive.Count == 0) AddItem();
        return inacive.Dequeue();
    }
    private void AddItem(int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            GameObject go = Instantiate(CE, transform);
            go.SetActive(false);
            SlowSmall ss = go.GetComponent<SlowSmall>();
            inacive.Enqueue(ss);
        }
    }
}
