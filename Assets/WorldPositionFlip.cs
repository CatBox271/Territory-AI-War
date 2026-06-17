using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WorldPositionFlip : MonoBehaviour
{
    Vector3 origin_pos;
    public bool Xpositive = false;
    public bool withX = false;

    public bool Ypositive = false;
    public bool withY = false;
    private void Awake()
    {
        origin_pos = transform.localPosition;
    }
    void Start()
    {
        if (withX)
        {
            if (Xpositive != transform.position.x > 0)
            {
                Xpositive = !Xpositive;
                origin_pos.x *= -1;
                transform.localPosition = origin_pos;
            }
        }
        if (withY)
        {
            if (Ypositive != transform.position.y > 0)
            {
                Ypositive = !Ypositive;
                origin_pos.y *= -1;
                transform.localPosition = origin_pos;
            }
        }
    }
}
