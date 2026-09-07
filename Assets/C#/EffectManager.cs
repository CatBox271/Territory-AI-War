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

    public void Boom(Vector3 pos, Color col, int count = 15, float speed = 5f, float startSize = 0.5f, float DieSpeed = 1, Vector2 baseDir = default, float spreadDeg = 360f)
    {
        for (int i = 0; i < count; i++)
        {
            GameObject go = Instantiate(CE, transform.parent);
            go.transform.position = pos;
            SlowSmall ss = go.GetComponent<SlowSmall>();

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
}
