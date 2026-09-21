using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// 对局数据记录：**每局一张宽表 CSV**，原始事件带"对局内时间"逐条追加，
/// 所以后面想按 1 秒 / 5 秒 / 一轮去聚合都行，记录端不做任何汇总。
///
/// 文件：Application.persistentDataPath/GameStats/对局_yyyy-MM-dd_HH-mm-ss.csv
/// 列：time,wall,round,kind,stage,other,unit,value_log2,value_text,count,ms,ms2,ms3,n1,n2,x,y,x2,y2,p0,p1,p2,p3,p4,note
///   time     对局内秒数（开局=0，真实时间，不受 timeScale / 录制暂停影响）
///   round    当前 AI 回合（AIAgent.CurrentRound）
///   kind     行的类型，见下面各 Note* 方法
///   stage    主体阵营（伤害行 = 攻击方；出局行 = 被杀方；领土行 = -1）
///   other    对方阵营（伤害行 = 受击方；出局行 = 凶手）
///   unit     单位/道具名（伤害行 = 来源单位，如 大球/穿甲/子弹；产出/出局行也用）
///   value_log2 / value_text   数值（大数用 log2 + 简写，如 12.34 / "5.06K"）
///   count    领土行 = 全图像素总数；弹珠行 = 该阵营当时活着的弹珠数；其它 = 1（次数）
///   ms/ms2/ms3  毫秒：移动耗时 / AI 的 totalMs、requestMs、behaviorMs
///   n1/n2    AI 行的 格式重试次数 / 请求发数
///   x,y/x2,y2   移动行的 起点、目标（move_end 里 x,y 是实际落点）
///   p0..p4  领土行的像素数（p0 = 中立，p1.. = 各阵营；列数按 MapConfig 的阵营数在开局定）
///   note    人类可读补充（原始描述、结束原因、工具名单、百分比……）
///
/// 钩子（各系统里各一行，见对应文件）：
///   InformGetter.AddDamage / AddMarble、PropTrigger.OnTriggerEnter2D、Marble.OnDestroy、
///   Towel.StartMove / StopMove、AIAgent.AppendTimingCsv / HandleStageDeath / 第 1 轮、GameEndMonitor.EndGame
/// </summary>
public class GameStats : MonoBehaviour
{
    public static GameStats Instance { get; private set; }

    /// <summary>文件夹名（在 Application.persistentDataPath 下）。</summary>
    public const string FolderName = "GameStats";

    /// <summary>领土快照的采样间隔（秒）。</summary>
    public const float TerritorySampleSeconds = 6f;

    // ---------- 状态 ----------
    private StreamWriter writer;
    private Stopwatch gameWatch;
    private bool running;
    private bool ended;
    private float territoryTimer;
    private int territoryColumnCount = 5;   // p0..p4（中立 + 4 阵营），开局按 MapConfig 的阵营数定
    private string currentPath = "";
    private static readonly Dictionary<int, double> moveStartMs = new();   // stage → 该次移动的开始时刻（对局内毫秒）

    public static bool IsGameRunning => Instance != null && Instance.running;
    public static string CurrentFilePath => Instance != null ? Instance.currentPath : "";
    public static float ElapsedSeconds => Instance != null && Instance.gameWatch != null
        ? (float)Instance.gameWatch.Elapsed.TotalSeconds
        : 0f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        var go = new GameObject("GameStatsRecorder");
        DontDestroyOnLoad(go);
        go.AddComponent<GameStats>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        CloseWriter();
    }

    private void OnApplicationQuit() => CloseWriter();

    private void Update()
    {
        if (!running)
        {
            // 还没开局就等一等：场景/角色卡就绪后自动开一份新记录（第 1 轮还会再调一次 BeginGame，幂等）
            if (ended || AIAgent.Instance == null) return;
            EnsureRunningInternal();
            if (!running) return;
        }

        // 领土：每个 TerritorySampleSeconds 秒采一次（真实时间）
        territoryTimer += Time.unscaledDeltaTime;
        if (territoryTimer >= TerritorySampleSeconds)
        {
            territoryTimer = 0f;
            NoteTerritorySample();
        }
    }

    /// <summary>第一次有玩法事件、或场景就绪时补开记录（已经在记 / 已经结束就什么都不做）。</summary>
    private void EnsureRunningInternal()
    {
        if (running || ended) return;
        BeginGameInternal();
    }

    // ==================== 开局 / 收尾 ====================

    /// <summary>新一局开始：建文件、写表头。第一颗弹珠注册时也会自动触发（幂等）。</summary>
    public static void BeginGame() => Instance?.BeginGameInternal();

    private void BeginGameInternal()
    {
        if (running) return;   // 已经在记了（例如开局弹珠先注册、第 1 轮再调一次）

        try
        {
            ended = false;
            running = true;
            gameWatch = Stopwatch.StartNew();
            territoryTimer = 0f;
            moveStartMs.Clear();

            int teams = MapConfig.Instance != null && MapConfig.Instance.teamColors != null
                ? MapConfig.Instance.teamColors.Count
                : 5;
            territoryColumnCount = Mathf.Clamp(teams, 2, 9);   // 下标 0 = 中立

            string dir = Path.Combine(Application.persistentDataPath, FolderName);
            Directory.CreateDirectory(dir);
            currentPath = Path.Combine(dir, $"对局_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");

            writer = new StreamWriter(currentPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(BuildHeader());

            int[] area = InformGetter.GetTerritoryArea();
            Row("start", territory: area, count: Sum(area), note: $"阵营数={territoryColumnCount - 1}，领土每 {TerritorySampleSeconds:0}s 采一次");
            Debug.Log($"[对局数据] 开始记录：{currentPath}");
        }
        catch (Exception e)
        {
            running = false;
            Debug.LogError($"[对局数据] 开局建文件失败：{e}");
        }
    }

    /// <summary>对局终止：补一行 end（含终局领土、赢家、总轮数、总时长）并把文件收掉。</summary>
    public static void EndGame(int winnerStage, bool pretendWin = false) => Instance?.EndGameInternal(winnerStage, pretendWin);

    private void EndGameInternal(int winnerStage, bool pretendWin)
    {
        if (!running || ended) return;
        ended = true;

        try
        {
            int[] area = InformGetter.GetTerritoryArea();
            string note = $"总轮数={AIAgent.CurrentRound}，对局时长={ElapsedSeconds:0.0}s" +
                          (pretendWin ? "，手动终止（模拟胜利）" : "");
            Row("end", stage: winnerStage, territory: area, count: Sum(area), note: note);
            writer?.Flush();
        }
        catch (Exception e)
        {
            Debug.LogError($"[对局数据] 写 end 行失败：{e}");
        }
        finally
        {
            CloseWriter();
            Debug.Log($"[对局数据] 本局记录结束：{currentPath}");
        }
    }

    private void CloseWriter()
    {
        try { writer?.Flush(); writer?.Dispose(); } catch { /* 收尾失败就算了 */ }
        writer = null;
        running = false;
    }

    // ==================== 各类事件（各系统调用） ====================

    /// <summary>领土快照：p0..pN 是各阵营（含中立）的像素数。</summary>
    public static void NoteTerritorySample() => Instance?.NoteTerritorySampleInternal();

    private void NoteTerritorySampleInternal()
    {
        int[] area = InformGetter.GetTerritoryArea();
        if (area == null) return;

        int total = Sum(area);
        if (total <= 0) return;

        var sb = new StringBuilder();
        for (int stage = 1; stage < area.Length; stage++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(stage).Append("号=").Append((area[stage] * 100f / total).ToString("0.#", CultureInfo.InvariantCulture)).Append('%');
        }
        if (area.Length > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("中立=").Append((area[0] * 100f / total).ToString("0.#", CultureInfo.InvariantCulture)).Append('%');
        }

        Row("territory", territory: area, count: total, note: sb.ToString());
    }

    /// <summary>一次伤害结算（IStageValue.Hit 的唯一出口）：attacker 打 victim 掉了 amount。</summary>
    public static void NoteDamage(int attackerStage, int victimStage, string sourceDesc, HugeInt amount)
        => Instance?.NoteDamageInternal(attackerStage, victimStage, sourceDesc, amount);

    private void NoteDamageInternal(int attackerStage, int victimStage, string sourceDesc, HugeInt amount)
    {
        if (amount <= 0) return;
        EnsureRunningInternal();
        Row("damage",
            stage: attackerStage,
            other: victimStage,
            unit: UnitNameOf(sourceDesc, victimStage),
            log2: HugeInt.Log2(amount),
            text: amount.ToShortString(true),
            count: 1,
            note: sourceDesc);
    }

    /// <summary>弹珠撞道具区产出道具：数值 = 弹珠当时的数值。</summary>
    public static void NoteProduce(int stage, WeaponKind item, HugeInt value, uint marbleExponent)
        => Instance?.NoteProduceInternal(stage, item, value, marbleExponent);

    private void NoteProduceInternal(int stage, WeaponKind item, HugeInt value, uint marbleExponent)
    {
        EnsureRunningInternal();
        Row("produce",
            stage: stage,
            unit: item.ToString(),
            log2: HugeInt.Log2(value),
            text: value.ToShortString(true),
            count: 1,
            n1: (int)marbleExponent,
            note: $"产出弹珠指数={marbleExponent}，该阵营当前弹珠={AliveMarbles(stage)}");
    }

    /// <summary>弹珠注册（出生）：注册式记录，弹珠数由事件累加得出。</summary>
    public static void NoteMarbleAdd(int stage, Marble marble) => Instance?.NoteMarbleAddInternal(stage, marble);

    private void NoteMarbleAddInternal(int stage, Marble marble)
    {
        if (marble == null) return;
        EnsureRunningInternal();
        uint exp = marble.ValueExponent;
        HugeInt value = HugeInt.Pow(2, (int)exp);
        Row("marble_add",
            stage: stage,
            log2: HugeInt.Log2(value),
            text: value.ToShortString(true),
            count: AliveMarbles(stage),
            n1: (int)exp,
            note: $"弹珠指数={exp}");
    }

    /// <summary>弹珠销毁（出局/清场时）：只在对局还在记的时候才写。</summary>
    public static void NoteMarbleRemove(int stage, uint marbleExponent)
        => Instance?.NoteMarbleRemoveInternal(stage, marbleExponent);

    private void NoteMarbleRemoveInternal(int stage, uint marbleExponent)
    {
        if (!running) return;   // 收场拆场景时不要往文件里灌噪音
        Row("marble_remove",
            stage: stage,
            count: AliveMarbles(stage),
            n1: (int)marbleExponent,
            note: $"弹珠指数={marbleExponent}");
    }

    /// <summary>炮塔开始移动。</summary>
    public static void NoteMoveStart(int stage, Vector2 from, Vector2 target, float plannedDistance)
        => Instance?.NoteMoveStartInternal(stage, from, target, plannedDistance);

    private void NoteMoveStartInternal(int stage, Vector2 from, Vector2 target, float plannedDistance)
    {
        EnsureRunningInternal();
        moveStartMs[stage] = gameWatch != null ? gameWatch.Elapsed.TotalMilliseconds : 0.0;
        Row("move_start",
            stage: stage,
            x: from.x, y: from.y, x2: target.x, y2: target.y,
            note: $"计划距离={plannedDistance.ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    /// <summary>炮塔停止移动（抵达/撞墙/原地停下……）。</summary>
    public static void NoteMoveEnd(int stage, Vector2 at, string reason) => Instance?.NoteMoveEndInternal(stage, at, reason);

    private void NoteMoveEndInternal(int stage, Vector2 at, string reason)
    {
        double ms = 0;
        if (gameWatch != null && moveStartMs.TryGetValue(stage, out double start))
            ms = gameWatch.Elapsed.TotalMilliseconds - start;
        moveStartMs.Remove(stage);

        Row("move_end",
            stage: stage,
            x: at.x, y: at.y,
            ms: ms,
            note: reason);
    }

    /// <summary>一次 AI 往返的耗时（来自 AIAgent 的 RoundTiming，每卡每轮一条）。</summary>
    public static void NoteAiTiming(int round, int stage, double totalMs, double requestMs, double behaviorMs,
        int formatRetries, int sends, string tools)
        => Instance?.NoteAiTimingInternal(round, stage, totalMs, requestMs, behaviorMs, formatRetries, sends, tools);

    private void NoteAiTimingInternal(int round, int stage, double totalMs, double requestMs, double behaviorMs,
        int formatRetries, int sends, string tools)
    {
        Row("ai",
            stage: stage,
            round: round,
            ms: totalMs,
            ms2: requestMs,
            ms3: behaviorMs,
            n1: formatRetries,
            n2: sends,
            note: tools);
    }

    /// <summary>某个阵营出局。</summary>
    public static void NoteDeath(int stage, int killerStage, string weapon) => Instance?.NoteDeathInternal(stage, killerStage, weapon);

    private void NoteDeathInternal(int stage, int killerStage, string weapon)
    {
        EnsureRunningInternal();
        Row("death",
            stage: stage,
            other: killerStage,
            unit: weapon ?? "",
            note: killerStage > 0
                ? $"{AIAgent.GetStageName(stage)} 被 {AIAgent.GetStageName(killerStage)} 击杀"
                : $"{AIAgent.GetStageName(stage)} 出局");
    }

    // ==================== 写行 ====================

    private void Row(string kind, int stage = -1, int other = -1, string unit = "",
        double log2 = 0, string text = "", int count = 0,
        double ms = 0, double ms2 = 0, double ms3 = 0, int n1 = 0, int n2 = 0,
        float x = 0, float y = 0, float x2 = 0, float y2 = 0,
        int[] territory = null, int round = -1, string note = "")
    {
        if (!running || writer == null) return;

        try
        {
            var sb = new StringBuilder(160);
            sb.Append(N(gameWatch != null ? gameWatch.Elapsed.TotalSeconds : 0)).Append(',');
            sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append(',');
            sb.Append(round >= 0 ? round : AIAgent.CurrentRound).Append(',');
            sb.Append(kind).Append(',');
            sb.Append(stage).Append(',');
            sb.Append(other).Append(',');
            sb.Append(Escape(unit)).Append(',');
            sb.Append(log2 > 0 ? log2.ToString("0.###", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(Escape(text)).Append(',');
            sb.Append(count > 0 ? count.ToString(CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(ms > 0 ? ms.ToString("0.#", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(ms2 > 0 ? ms2.ToString("0.#", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(ms3 > 0 ? ms3.ToString("0.#", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(n1 != 0 ? n1.ToString(CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(n2 != 0 ? n2.ToString(CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(N(x)).Append(',');
            sb.Append(N(y)).Append(',');
            sb.Append(N(x2)).Append(',');
            sb.Append(N(y2)).Append(',');

            for (int i = 0; i < territoryColumnCount; i++)
            {
                bool has = territory != null && i < territory.Length;
                sb.Append(has ? territory[i].ToString(CultureInfo.InvariantCulture) : "").Append(',');
            }

            sb.Append(Escape(note));
            writer.WriteLine(sb.ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[对局数据] 写 {kind} 行失败：{e.Message}");
        }
    }

    private string BuildHeader()
    {
        var sb = new StringBuilder();
        sb.Append("time,wall,round,kind,stage,other,unit,value_log2,value_text,count,ms,ms2,ms3,n1,n2,x,y,x2,y2");
        for (int i = 0; i < territoryColumnCount; i++) sb.Append(",p").Append(i);
        sb.Append(",note");
        return sb.ToString();
    }

    // ==================== 小工具 ====================

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static int Sum(int[] area)
    {
        if (area == null) return 0;
        int total = 0;
        for (int i = 0; i < area.Length; i++) total += area[i];
        return total;
    }

    /// <summary>该阵营当前活着的弹珠数（注册表里 AliveCheck 过的）。</summary>
    private static int AliveMarbles(int stage)
    {
        if (!InformGetter.MarbleItems.TryGetValue(stage, out var list) || list == null) return 0;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && list[i].AliveCheck()) n++;
        return n;
    }

    /// <summary>从 "3号阵营大球" 这种描述里取单位名（大球/穿甲/子弹……），取不出来就原样返回。</summary>
    private static string UnitNameOf(string sourceDesc, int victimStage)
    {
        if (string.IsNullOrEmpty(sourceDesc)) return "";
        int i = sourceDesc.LastIndexOf("号阵营", StringComparison.Ordinal);
        if (i < 0) return sourceDesc;
        string unit = sourceDesc.Substring(i + "号阵营".Length).Trim();
        return unit.Length > 0 ? unit : sourceDesc;
    }

    /// <summary>CSV 转义：逗号换全角、去掉换行与引号。</summary>
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\n', ' ').Replace('\r', ' ').Replace(',', '，').Replace('"', '\'');
    }
}
