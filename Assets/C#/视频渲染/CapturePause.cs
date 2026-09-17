using RenderHeads.Media.AVProMovieCapture;
using UnityEngine;

public static class CapturePause
{
    public static CaptureBase Capture { get; set; }

    public static bool IsCapturing => Capture != null && Capture.IsCapturing();
    public static bool IsPaused => Capture != null && Capture.IsPaused();

    /// <summary>
    /// 暂停前的 Time.timeScale。AVPro 的 PauseCapture / ResumeCapture 在非实时录制时会直接把 timeScale 写成 0 / 1
    /// （插件源码里那句 TODO 自己都承认「不该假设恢复时 timeScale 是 1」），于是「舞台演出期间冻结战场」会被
    /// 一次“暂停录制→恢复录制”解开：升级选择正好是在演出中途暂停录制去问 AI，AI 一答完 ResumeCapture 就把
    /// timeScale 顶回 1，舞台还没播完战场就活了。这里记下暂停前的值，恢复之后还原回去。
    /// </summary>
    private static float timeScaleBeforePause = 1f;
    private static bool hasTimeScaleBeforePause;

    /// <summary>暂停录制（非实时录制时 AVPro 会把 Time.timeScale 压成 0，用来停帧）</summary>
    public static void Pause()
    {
        if (Capture != null && Capture.IsCapturing() && !Capture.IsPaused())
        {
            timeScaleBeforePause = Time.timeScale;
            hasTimeScaleBeforePause = true;
            Capture.PauseCapture();
        }
    }

    /// <summary>恢复录制，并把 Time.timeScale 还原成暂停前的值（不是无脑设成 1）</summary>
    public static void Resume()
    {
        if (Capture != null && Capture.IsCapturing() && Capture.IsPaused())
        {
            Capture.ResumeCapture();
            if (hasTimeScaleBeforePause) Time.timeScale = timeScaleBeforePause;
        }
    }
}
