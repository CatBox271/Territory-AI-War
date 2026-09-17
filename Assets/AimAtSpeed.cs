using UnityEngine;

/// <summary>
/// 让 tf 的 **up（本地 +Y）方向**始终对齐刚体速度方向。
/// 弹体美术（穿甲弹.png）弹尖就画在 +Y，所以"up 朝哪，弹尖就朝哪"。
///
/// 这里不用自己推 sin/cos：直接问 Unity「从当前 up 转到速度方向要转几度」，
/// 再把 tf 转过去。这样不存在符号/约定写反的可能（之前就是手推那一步的符号写错）。
/// </summary>
public class AimAtSpeed : MonoBehaviour
{
    public Transform tf;
    public Rigidbody2D rb;

    [Tooltip("勾上会为每条弹打印一次诊断（速度 / 转向角 / 转完的 up 与速度的点积），用来确认朝向")]
    public bool debugLogOnce = true;

    private bool _logged;

    private void FixedUpdate()
    {
        if (tf == null || rb == null) return;

        Vector2 v = rb.velocity;
        if (v.sqrMagnitude < 0.000001f) return;   // 几乎静止：保持上一帧朝向，别乱转

        // Unity 自带的有符号夹角（逆时针为正）：从"现在的 up"转到"速度方向"还差多少度
        float delta = Vector2.SignedAngle(tf.up, v);
        tf.rotation = Quaternion.Euler(0f, 0f, tf.eulerAngles.z + delta);

        if (debugLogOnce && !_logged)
        {
            _logged = true;
            Vector2 vDir = v.normalized;
        }
    }
}
