using System.Collections;
using RenderHeads.Media.AVProMovieCapture;
using UnityEngine;

/// <summary>
/// 占地达标自动结束组件：
/// 定期扫描 TerritoryCanvas 的领地网格，当某个非 0 阵营的占地面积达到 endThresholdPercent（默认 100%），
/// 并且【全部 &&】：只剩一个阵营（且就是它）、场上没有敌方游离道具（大球/子弹）、赢家终局感言已播出时，
/// 自动停止 AI 循环、停止录制并结束游戏（冻结全局时间）。
/// 终止时顺便让 GameMemory 把各阵营这一局的记忆追加写盘（每阵营一个文件）。
/// 录制不归它管：要么自己开 CaptureBase 的 _captureOnStart，要么用本组件的右键菜单「启动录制」。
/// 提供测试按钮（OnGUI 游戏视口左上角）与右键菜单入口，可直接结束录制并结束游戏。
/// </summary>
public class GameEndMonitor : MonoBehaviour
{
    public static GameEndMonitor Instance { get; private set; }

    [Tooltip("扫描间隔（秒）。每个间隔对整张地图做一次完整性扫描")]
    public float checkInterval = 1f;

    [Tooltip("是否在游戏视口左上角显示测试按钮")]
    public bool showTestButton = true;

    [Tooltip("结束后延迟多少秒退出应用；0 = 不退出（编辑器里始终不退出）")]
    public float quitDelaySeconds = 0f;

    [Tooltip("某阵营占地比例达到该百分比即判定其获胜并结束（0-100）。100 = 全图每一个像素都归它")]
    [Range(0f, 100f)]
    public float endThresholdPercent = 100f;

    /// <summary>是否已经结束过（幂等保护）。</summary>
    public bool GameEnded { get; private set; }

    private float _timer;
    private bool _captureMissingLogged;
    private bool _recordStartFailedLogged;
    private string _lastWaitReason;

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (GameEnded) return;

        _timer += Time.deltaTime;
        if (_timer < checkInterval) return;
        _timer = 0f;

        int owner = FindOwnerOverThreshold();
        if (owner <= 0) return;

        // 完整结束判据（全部 &&）：占地达标（上面已过，默认 100%）&& 只剩一个阵营 && 无敌方游离道具（大球/子弹）&& 赢家终局感言已播出
        if (AIAgent.Instance == null) return;
        if (!AIAgent.Instance.IsEndgameReady(owner, out string reason))
        {
            if (_lastWaitReason != reason)
            {
                _lastWaitReason = reason;
                Debug.Log($"[GameEndMonitor] 阵营 {owner} 占地已过 {endThresholdPercent:0.#}%，但还不满足结束条件：{reason}");
            }
            return;
        }

        Debug.Log($"[GameEndMonitor] 阵营 {owner} 占地 {endThresholdPercent:0.#}% 且场上已无敌方游离道具，结束录制并结束游戏");
        EndGame(owner);
    }

    /// <summary>
    /// 扫描领地网格：统计各阵营占地像素数，占比最高者达到 endThresholdPercent（默认 100%，
    /// 即整张网格每一个像素都归它、中立 0 地块也算没占满）
    /// 时返回该阵营编号，否则返回 0。地图未就绪时也返回 0。
    /// </summary>
    public int FindOwnerOverThreshold()
    {
        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return 0;

        var map = canvas.territoryMap;
        int total = map.Length;
        if (total == 0) return 0;

        // 阵营编号是 byte 且数量很少，直接开表计数（0 = 中立/未占领，一起统计）
        var counts = new int[256];
        for (int i = 0; i < total; i++)
            counts[map[i]]++;

        int owner = 0;
        int ownerCount = 0;
        for (int stage = 1; stage < counts.Length; stage++)
        {
            if (counts[stage] > ownerCount)
            {
                ownerCount = counts[stage];
                owner = stage;
            }
        }
        if (owner == 0) return 0;

        float percent = ownerCount * 100f / total;
        return percent >= endThresholdPercent ? owner : 0;
    }

    /// <summary>测试入口：直接结束录制并结束游戏。</summary>
    [ContextMenu("结束录制并结束游戏")]
    public void TestEndGame()
    {
        EndGame(0);
    }

    /// <summary>测试入口：手动"像有人赢了一样"结束（没点名赢家就拿当前占地最多的阵营当赢家）。</summary>
    [ContextMenu("像有人赢了一样结束录制")]
    public void TestEndGameAsWin()
    {
        EndGame(0, pretendWin: true);
    }

    /// <summary>
    /// 扫一次领地网格：leader = 占地最多的非 0 阵营（都没占就 0），leaderPercent = 它的占比，
    /// percentByStage[stage] = 各阵营占比。地图没就绪时返回 false。
    /// </summary>
    private static bool ScanTerritory(out int leader, out float leaderPercent, out float[] percentByStage)
    {
        leader = 0;
        leaderPercent = 0f;
        percentByStage = null;

        var canvas = TerritoryCanvas.Instance;
        if (canvas == null || !canvas.territoryMap.IsCreated) return false;

        var map = canvas.territoryMap;
        int total = map.Length;
        if (total == 0) return false;

        var counts = new int[256];
        for (int i = 0; i < total; i++)
            counts[map[i]]++;

        percentByStage = new float[counts.Length];
        int bestStage = 0;
        int bestCount = 0;
        for (int stage = 1; stage < counts.Length; stage++)
        {
            percentByStage[stage] = counts[stage] * 100f / total;
            if (counts[stage] > bestCount)
            {
                bestCount = counts[stage];
                bestStage = stage;
            }
        }

        leader = bestStage;
        leaderPercent = bestCount * 100f / total;
        return true;
    }

    /// <summary>
    /// 结束录制并结束游戏（幂等）。winnerStage &gt; 0 时附带获胜播报。
    /// pretendWin = 手动终止时"像有人赢了一样"：winnerStage &lt;= 0 就拿当前占地最多的阵营当赢家，
    /// 播报按它真实的占地百分比，记忆里也按"它赢了"来写 —— 只影响播报与记忆，不动胜负判定本身。
    /// 流程：停 AI 循环 → 播报结果 → 发起「终局记忆」写盘 → 下一帧渲染后停止录制 → 冻结全局时间 → 可选延迟退出。
    /// </summary>
    public void EndGame(int winnerStage, bool pretendWin = false)
    {
        if (GameEnded) return;
        GameEnded = true;

        // 手动终止想装成"有人赢"：没点名赢家就用现在占地最多的那个
        int winner = winnerStage;
        float winnerPercent = 0f;
        if (pretendWin && ScanTerritory(out int leader, out float leaderPercent, out float[] percentByStage))
        {
            if (winner <= 0)
            {
                winner = leader;
                winnerPercent = leaderPercent;
            }
            else if (winner < percentByStage.Length)
            {
                winnerPercent = percentByStage[winner];
            }
        }

        Debug.Log($"[GameEndMonitor] 结束录制并结束游戏 (winner={winnerStage}, pretendWin={pretendWin}, 实际赢家={winner})");

        // 1. 停止 AI 决策循环，避免继续发起请求
        if (AIAgent.Instance != null)
            AIAgent.Instance.StopCycle();

        // 2. 播报结果（先于停录，让最终一帧录到获胜画面）
        string msg;
        if (winner > 0)
        {
            msg = pretendWin
                ? $"游戏结束：{AIAgent.GetStageName(winner)} 占地 {winnerPercent:0.#}% 获得胜利！"
                : $"游戏结束：{AIAgent.GetStageName(winner)} 占地 {endThresholdPercent:0.#}% 获得胜利！";
        }
        else
        {
            msg = "游戏结束";
        }
        UISystemMessageShow.ShowNow(msg);

        // 3. 终局记忆：每个阵营把自己这一局的记忆压成一段长期记忆，追加写进 Memory 文件夹。
        //    只在这里发请求，不阻塞停止录制；真的要退出应用时再等它写完（见 FinishShutdown）。
        _memoryTask = GameMemory.GenerateAllAsync(winner);

        StartCoroutine(FinishShutdown());
    }

    /// <summary>终局记忆的写盘任务（可能还在飞）。</summary>
    private System.Threading.Tasks.Task _memoryTask;

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

        // 4. 冻结全局时间，结束游戏（录制已经停了，记忆请求不受 timeScale 影响）
        Time.timeScale = 0f;

        // 5. 可选：用真实时间延迟退出应用（timeScale 冻结不影响）
        if (quitDelaySeconds > 0f)
        {
            // 退出前先把终局记忆写完，别把文件丢了（请求自身有超时，不会一直等）
            while (_memoryTask != null && !_memoryTask.IsCompleted)
                yield return null;

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
