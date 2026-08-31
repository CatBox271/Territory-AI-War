using System.Collections;
using RenderHeads.Media.AVProMovieCapture;
using UnityEngine;

/// <summary>
/// 录制 + 地图涂满自动结束组件：
/// 开局自动启动录制（CapturePause.Capture 未赋值时自动查找并回填）；
/// 定期扫描 TerritoryCanvas 的领地网格，当某个非 0 阵营把整张地图涂满时，
/// 自动停止 AI 循环、停止录制并结束游戏（冻结全局时间）。
/// 提供测试按钮（OnGUI 游戏视口左上角）与右键菜单入口，可直接结束录制并结束游戏。
/// </summary>
public class GameEndMonitor : MonoBehaviour
{
    public static GameEndMonitor Instance { get; private set; }

    [Tooltip("扫描间隔（秒）。每个间隔对整张地图做一次完整性扫描")]
    public float checkInterval = 1f;

    [Tooltip("是否在游戏视口左上角显示测试按钮")]
    public bool showTestButton = true;

    [Tooltip("开局是否自动启动录制（对已录制的状态幂等，不会重复启动）")]
    public bool startRecordingOnStart = true;

    [Tooltip("结束后延迟多少秒退出应用；0 = 不退出（编辑器里始终不退出）")]
    public float quitDelaySeconds = 0f;

    /// <summary>是否已经结束过（幂等保护）。</summary>
    public bool GameEnded { get; private set; }

    private float _timer;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        if (startRecordingOnStart)
            StartRecording();
    }

    /// <summary>
    /// 启动录制（幂等）：优先复用 CapturePause.Capture，未赋值时按与 AIAgent 相同的方式
    /// 查找场景中的 CaptureBase 并回填。若录制组件自身的 _captureOnStart 已经启动，这里不会重复启动。
    /// </summary>
    [ContextMenu("启动录制")]
    public void StartRecording()
    {
        var capture = CapturePause.Capture;
        if (capture == null)
            capture = FindObjectOfType<CaptureBase>();
        if (capture == null)
        {
            Debug.LogWarning("[GameEndMonitor] 未找到录制组件（CaptureBase），录制未启动");
            return;
        }
        if (CapturePause.Capture == null)
            CapturePause.Capture = capture;
        if (capture.IsCapturing())
        {
            Debug.Log("[GameEndMonitor] 录制已在运行，无需启动");
            return;
        }
        if (capture.StartCapture())
            Debug.Log("[GameEndMonitor] 录制已启动");
        else
            Debug.LogError("[GameEndMonitor] 录制启动失败（StartCapture 返回 false）");
    }

    private void Update()
    {
        if (GameEnded) return;
        _timer += Time.deltaTime;
        if (_timer < checkInterval) return;
        _timer = 0f;

        int winner = FindFullMapOwner();
        if (winner > 0)
        {
            Debug.Log($"[GameEndMonitor] 阵营 {winner} 已涂满整张地图，结束录制并结束游戏");
            EndGame(winner);
        }
    }

    /// <summary>
    /// 扫描领地网格：所有像素都属于同一个非 0 阵营时返回该阵营编号，否则返回 0。
    /// 地图未就绪时也返回 0。
    /// </summary>
    public int FindFullMapOwner()
    {
        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return 0;

        var map = canvas.territoryMap;
        byte owner = 0;
        for (int i = 0; i < map.Length; i++)
        {
            byte stage = map[i];
            if (stage == 0) return 0;            // 仍有未占领像素
            if (owner == 0) owner = stage;       // 记下第一个出现的阵营
            else if (stage != owner) return 0;   // 出现第二个阵营
        }
        return owner;
    }

    /// <summary>测试入口：直接结束录制并结束游戏。</summary>
    [ContextMenu("结束录制并结束游戏")]
    public void TestEndGame()
    {
        EndGame(0);
    }

    /// <summary>
    /// 结束录制并结束游戏（幂等）。winnerStage &gt; 0 时附带获胜播报。
    /// 流程：停 AI 循环 → 播报结果 → 下一帧渲染后停止录制 → 冻结全局时间 → 可选延迟退出。
    /// </summary>
    public void EndGame(int winnerStage)
    {
        if (GameEnded) return;
        GameEnded = true;
        Debug.Log($"[GameEndMonitor] 结束录制并结束游戏 (winner={winnerStage})");

        // 1. 停止 AI 决策循环，避免继续发起请求
        if (AIAgent.Instance != null)
            AIAgent.Instance.StopCycle();

        // 2. 播报结果（先于停录，让最终一帧录到获胜画面）
        string msg = winnerStage > 0
            ? $"游戏结束：{AIAgent.GetStageName(winnerStage)} 涂满地图获得胜利！"
            : "游戏结束";
        UISystemMessageShow.ShowNow(msg);

        StartCoroutine(FinishShutdown());
    }

    private IEnumerator FinishShutdown()
    {
        // 让获胜播报这一帧先渲染进录制
        yield return new WaitForEndOfFrame();

        // 3. 停止录制：若正处于暂停（AI 请求中）先恢复一帧，再同步收尾写文件
        var capture = CapturePause.Capture;
        if (capture != null)
        {
            if (capture.IsCapturing() && capture.IsPaused())
                CapturePause.Resume();
            if (capture.IsCapturing())
                capture.StopCapture();
            Debug.Log("[GameEndMonitor] 录制已停止");
        }
        else
        {
            Debug.LogWarning("[GameEndMonitor] 未找到录制组件（CapturePause.Capture 为空）");
        }

        // 4. 冻结全局时间，结束游戏
        Time.timeScale = 0f;

        // 5. 可选：用真实时间延迟退出应用（timeScale 冻结不影响）
        if (quitDelaySeconds > 0f)
        {
            float t = 0f;
            while (t < quitDelaySeconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            Application.Quit();
        }
    }

    // 测试按钮：游戏视口左上角
    private void OnGUI()
    {
        if (!showTestButton || GameEnded) return;
        GUILayout.BeginArea(new Rect(10f, 10f, 220f, 40f));
        if (GUILayout.Button("结束录制并结束游戏（测试）"))
            TestEndGame();
        GUILayout.EndArea();
    }
}
