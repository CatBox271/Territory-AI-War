using System.Collections;
using System.Collections.Generic;
using UnityEngine;

//负责持续锁定目标
public class AimController : MonoBehaviour
{
    public AutoRotater auto;
    public Towel towel;
    public float MaxDis = 4f;
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

    /// <summary>是否正处于 AI/道具的持续瞄准控制中。</summary>
    public bool IsControlling => aim != null;

    /// <summary>开始持续瞄准目标。目标无效时返回 false。</summary>
    public bool StartControl(ItemType target)
    {
        if (target == null || target.item == null) return false;
        ChangeAim(target);
        if (auto != null && auto.active)
            auto.active = false;
        return true;
    }

    /// <summary>停止瞄准控制，把炮塔交还给 AutoRotater 自动旋转。</summary>
    public void StopControl()
    {
        DisConnect();
        if (auto != null && !auto.active)
            auto.active = true;
    }

    void DisConnect()
    {
        aim = null;
    }

    /// <summary>自动断开（超时/超距/目标消失）时调用：记录提示供下一轮 GetInfo 输出。</summary>
    void DisconnectWithNotice(string reason)
    {
        DisConnect();
        if (towel != null)
            InformGetter.NotifyTurretControlLost(towel.stage, reason);
    }

    bool AimLoop()
    {
        if (aim == null) return false;
        if (aim.item == null)
        {
            DisconnectWithNotice("目标已消失");
            return false;
        }
        pos = aim.pos;
        if ((pos - self).sqrMagnitude > SqrDis)
        {
            DisconnectWithNotice("目标超出炮塔最大跟踪距离");
            return false;
        }
        if (Time.time - aimTime > MaxAimTime)
        {
            DisconnectWithNotice("持续瞄准超时");
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
