using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//负责持续锁定目标
public class AimController : MonoBehaviour
{
    public AutoRotater auto;
    public Towel towel;
    private float MaxDis = 4f;
    private float SqrDis;
    private float MaxAimTime = 12f;
    ItemType aim;
    void LookAt(Vector2 pos) => towel.LookAt(pos);

    private void Start()
    {
        SqrDis = MaxDis * MaxDis;
    }
    //无控制状态自动切换为旋转

    Vector2 pos = new();
    Vector2 self
    {
        get 
        {
            Vector3 v3 = transform.position;
            return new(v3.x, v3.y);
        }
    }

    float aimTime = new();
    public void ChangeAim(ItemType Iaim)
    {
        aimTime = Time.time;
        aim = Iaim;
    }

    void DisConnect()
    {
        aim = null;
    }

    bool AimLoop()
    {
        if (aim == null) return false;
        pos = aim.pos;
        if ((pos - self).sqrMagnitude > SqrDis)
        {
            DisConnect();
            return false;
        }
        if (Time.time - aimTime > MaxAimTime)
        {
            DisConnect();
            return false;
        }
        LookAt(aim.pos);
        return true;
    }
    private void Update()
    {
        if (!AimLoop())
        {
            if (!auto.active)
            {
                auto.active = true;
            }
        }
        else
        {
            if (auto.active)
            {
                auto.active = false;
            }
        }
    }
}
