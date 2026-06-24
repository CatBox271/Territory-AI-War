using RenderHeads.Media.AVProMovieCapture;

public static class CapturePause
{
    public static CaptureBase Capture { get; set; }

    public static bool IsCapturing => Capture != null && Capture.IsCapturing();
    public static bool IsPaused => Capture != null && Capture.IsPaused();

    /// <summary>暂停录制（不改变 Time.timeScale）</summary>
    public static void Pause()
    {
        if (Capture != null && Capture.IsCapturing() && !Capture.IsPaused())
            Capture.PauseCapture();
    }

    /// <summary>恢复录制</summary>
    public static void Resume()
    {
        if (Capture != null && Capture.IsCapturing() && Capture.IsPaused())
            Capture.ResumeCapture();
    }
}
