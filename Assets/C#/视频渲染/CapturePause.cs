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

    /// <summary>
    /// 录制被暂停后的现实时间（Time.realtimeSinceStartup）。用来做「卡住太久就强制恢复」的兜底。
    /// </summary>
    private static float pauseStartedAt = -1f;
    private static bool stuckLogged;

    /// <summary>
    /// 兜底阈值（现实秒）：一次暂停超过这么久还没恢复，就认为请求/等待链路挂了。
    /// 正常一轮 AI 决策最多几十秒，比赛前的狠话那一轮稍长，但都远小于这个数；
    /// 调小会误伤慢请求，调大只是多冻一会儿，改这里就行。
    /// </summary>
    public static float StuckTimeoutSeconds = 300f;

    /// <summary>暂停录制（非实时录制时 AVPro 会把 Time.timeScale 压成 0，用来停帧）</summary>
    public static void Pause()
    {
        if (Capture != null && Capture.IsCapturing() && !Capture.IsPaused())
        {
            timeScaleBeforePause = Time.timeScale;
            hasTimeScaleBeforePause = true;
            Capture.PauseCapture();

            pauseStartedAt = Time.realtimeSinceStartup;
            stuckLogged = false;
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
        pauseStartedAt = -1f;
        stuckLogged = false;
    }

    /// <summary>
    /// 兜底看门狗，由 AIAgent.Update 每帧调用。
    ///
    /// 录制停止恢复的已知原因只有一个：某条 await 永远不会回来。所有 Pause 都写在 try/finally 里，
    /// 异常路径能恢复，但「一直挂着不返回」时 finally 根本不会执行——最典型的是
    /// ValidateRoundReply 校验失败时 SendRequest 重发、同时返回 false 让原来那次不回调，
    /// 于是 await tcs.Task 的循环等不到结果；另外遗言/获奖感言那条 TCS 也没有超时。
    /// 那种情况下录制会一直停着（timeScale 也是 0），画面看上去就是「游戏卡死」。
    ///
    /// 所以这里只做最后一层保险：暂停超过 StuckTimeoutSeconds 仍未恢复，就强制恢复一次并打警告。
    /// 它不是正常流程的一部分，正常跑起来永远不该触发（触发了说明上面那条链路有 bug）。
    /// </summary>
    public static void Tick()
    {
        if (pauseStartedAt < 0f) return;
        if (Capture == null || !Capture.IsCapturing() || !Capture.IsPaused())
        {
            // 录制已经不在暂停态（被停录、或别处直接调了 ResumeCapture），清掉计时
            pauseStartedAt = -1f;
            stuckLogged = false;
            return;
        }

        float pausedFor = Time.realtimeSinceStartup - pauseStartedAt;
        if (pausedFor < StuckTimeoutSeconds) return;

        if (!stuckLogged)
        {
            stuckLogged = true;
            Debug.LogError($"[CapturePause] 录制已暂停 {pausedFor:0.#}s 仍未恢复（超过 {StuckTimeoutSeconds:0.#}s），" +
                           "判定某条等待链路卡住，强制恢复录制与 Time.timeScale。请查上一条 AI 请求/等待的日志。");
        }
        Resume();
    }
}
