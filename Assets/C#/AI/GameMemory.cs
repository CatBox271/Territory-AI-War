using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// 角色卡是 AIAgent 的嵌套类型，这里起个别名，省得到处写 AIAgent.CharacterCard
using CharacterCard = AIAgent.CharacterCard;

/// <summary>
/// 终局记忆：一局结束（GameEndMonitor.EndGame）时，把每个 AI **自己这一局的记忆**（它自己的 history：
/// 收到的情报、对手说过的话、它的内心独白与行动）交给 ds-flash 压成一段高度压缩的第一人称长期记忆，
/// 重点保留「被谁骗过 / 跟谁结过盟 / 被谁杀了」的景象，然后追加写进
/// persistentDataPath/Memory/&lt;N号_角色名&gt;.txt —— 每阵营一个文件，一局追加一段（追加式，永不覆盖）。
///
/// 只写盘、不回读：不碰 MapConfig.lastGameRecap，也不改任何提示词。
/// </summary>
public static class GameMemory
{
    /// <summary>记忆文件夹名（在 Application.persistentDataPath 下）。</summary>
    public const string FolderName = "Memory";

    /// <summary>压缩记忆用的模型（和行为检查退回路径同一档，便宜、够用）。</summary>
    private const string Model = "deepseek-flash";

    /// <summary>输出上限：提示词只要求 400 字，这里留足余量，免得模型多写两句就被截成半句写进文件。</summary>
    private const int MaxTokens = 4096;

    /// <summary>单张卡的记忆请求超时（秒，真实时间，不受 Time.timeScale 影响）。</summary>
    private const float RequestTimeoutSeconds = 120f;

    /// <summary>喂给模型的记忆正文上限（字符）：超了就留开头（结盟、试探）和结尾（怎么死的），中间掐掉。</summary>
    private const int TranscriptCharBudget = 60000;

    private const string FallbackApiUrl = "https://api.deepseek.com/v1/chat/completions";

    public static string FolderPath => Path.Combine(Application.persistentDataPath, FolderName);

    /// <summary>某个阵营的记忆文件：Memory/&lt;N号_角色名&gt;.txt（角色名里的非法文件名字符会被去掉）。</summary>
    public static string FilePathFor(CharacterCard card)
    {
        int stage = card != null ? card.position : 0;
        string name = card != null ? SanitizeFileName(card.name) : "";
        string file = name.Length > 0 ? $"{stage}号_{name}.txt" : $"{stage}号.txt";
        return Path.Combine(FolderPath, file);
    }

    // ==================== 本局客观事件（全体共享） ====================

    /// <summary>一次出局的客观记录：谁、第几轮、被谁用什么杀的。</summary>
    private struct DeathRecord
    {
        public int stage;
        public int killerStage;
        public string weapon;
        public int round;
    }

    private static readonly List<DeathRecord> deaths = new();

    /// <summary>某个阵营被击杀时调用（AIAgent.HandleStageDeath 里，去重之后）：只记客观事实，不做判断。</summary>
    public static void NoteDeath(int stage, int killerStage, string killerWeapon)
    {
        deaths.Add(new DeathRecord
        {
            stage = stage,
            killerStage = killerStage,
            weapon = string.IsNullOrWhiteSpace(killerWeapon) ? "" : killerWeapon.Trim(),
            round = AIAgent.CurrentRound
        });
    }

    /// <summary>新一局开始时清空上一局的出局记录（AIAgent 在第 1 轮调）。</summary>
    public static void ResetForNewGame() => deaths.Clear();

    // ==================== 入口 ====================

    /// <summary>
    /// 一局终止时调用：每个阵营各自生成一段本局记忆并追加写盘。
    /// 全部内部捕获异常，绝不把异常抛回终止流程；返回的 Task 只在真要退出应用时才需要等。
    /// </summary>
    public static async Task GenerateAllAsync(int winnerStage)
    {
        try
        {
            AIAgent agent = AIAgent.Instance;
            if (agent == null || agent.cards == null || agent.cards.Count == 0)
            {
                Debug.LogWarning("[终局记忆] 没有 AIAgent / 角色卡，跳过本局记忆");
                return;
            }

            string key = LoadApiKey();
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogWarning("[终局记忆] 没有找到 Key.txt，跳过本局记忆");
                return;
            }

            // 全体共享的对局情况：只算一次，四个阵营的文件里写的都是同一份
            string shared = BuildSharedSituation(winnerStage);

            var tasks = new List<Task>();
            foreach (CharacterCard card in agent.cards)
                if (card != null) tasks.Add(RememberOneAsync(card, winnerStage, key, shared));

            await Task.WhenAll(tasks);
            deaths.Clear();   // 这一局的出局记录已经写进文件，清掉，别带进下一局
            Debug.Log($"[终局记忆] 本局记忆已全部追加到 {FolderPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[终局记忆] 生成本局记忆时异常：{e}");
        }
    }

    // ==================== 单张卡 ====================

    private static async Task RememberOneAsync(CharacterCard card, int winnerStage, string key, string shared)
    {
        try
        {
            string transcript = BuildTranscript(card);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Debug.LogWarning($"[终局记忆] {card.name}（{card.position}号）这一局没有任何记忆内容，跳过");
                return;
            }

            string outcome = DescribeOutcome(card, winnerStage);

            DeepSeekRequest request = new DeepSeekRequest
            {
                model = Model,
                temperature = 0.3f,
                max_tokens = MaxTokens,
                stream = false,
                thinking = new ThinkingConfig(false)
            };
            request.messages = new List<DeepSeekMessage>
            {
                new DeepSeekMessage("system", SystemPrompt),
                new DeepSeekMessage("user", BuildUserPrompt(card, outcome, transcript))
            };
            request.tools = null;
            request.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new RequestInfo(
                request,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m != null && m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? last.content : "");
                },
                error =>
                {
                    Debug.LogWarning($"[终局记忆] {card.name}（{card.position}号）的记忆请求失败：{error}");
                    tcs.TrySetResult("");
                },
                toolkit: null,
                back_tool: false,
                toolStage: card.position);
            info.apiKey = key;
            info.apiUrl = string.IsNullOrWhiteSpace(card.url) ? FallbackApiUrl : card.url;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            AIRequest.SendRequest(info);

            Task<string> send = tcs.Task;
            Task finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(RequestTimeoutSeconds)));
            sw.Stop();

            string memory;
            if (finished != send)
            {
                Debug.LogWarning($"[终局记忆] {card.name}（{card.position}号）的记忆请求超过 {RequestTimeoutSeconds:0}s 没回来，这一段只写共享对局情况");
                memory = "（这一局的角色记忆没能生成：请求超时）";
            }
            else
            {
                memory = (send.Result ?? "").Trim();
                if (memory.Length == 0)
                {
                    Debug.LogWarning($"[终局记忆] {card.name}（{card.position}号）的记忆请求没有返回内容，这一段只写共享对局情况");
                    memory = "（这一局的角色记忆没能生成）";
                }
            }

            AppendToFile(card, outcome, memory, shared);
            Debug.Log($"[终局记忆] {card.name}（{card.position}号）的本局记忆已追加（{sw.Elapsed.TotalSeconds:F1}s）：\n{memory}\n{shared}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[终局记忆] {card.name}（{(card != null ? card.position : 0)}号）生成记忆异常：{e}");
        }
    }

    // ==================== 提示词 ====================

    private const string SystemPrompt =
        "你是某个游戏 AI 的长期记忆。这一局刚刚结束，请把下面这份**它自己这一局亲历的记忆**" +
        "压缩成一段它以后会一直记住的经历。\n" +
        "要求：\n" +
        "1. 第一人称（“我”），像它自己在回忆，不要写“该 AI”“它”这种旁观说法。\n" +
        "2. 高度压缩：其它经历只用一两句话带过，但这几件事必须写成**具体的小场景**" +
        "（谁 + 当时说了什么/做了什么 + 结果 + 我当时的心情）：\n" +
        "   · 被欺骗、被背叛：谁对我许了什么、后来怎么反悔、背后做了什么；\n" +
        "   · 结盟：跟谁结的盟、谁先提的、有没有被遵守；\n" +
        "   · 被杀：谁杀了我、用什么杀的、我当时在哪、我最后说了什么；\n" +
        "   · 我骗过、背叛过谁（如果有）。\n" +
        "3. 严格按下面五行输出，每行都以标签开头；没发生的那行写“无”：\n" +
        "结局：…\n欺骗：…\n结盟：…\n被杀：…\n其他：…\n" +
        "4. 不要写数字统计、不要写建议、不要客套、不要复述情报原文。\n" +
        "5. 只写我那时候能知道的事——我没看到、没被告知的事不要编。";

    private static string BuildUserPrompt(CharacterCard card, string outcome, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【这一局的结局】{outcome}");
        sb.AppendLine();
        sb.AppendLine($"【我（{card.name}，{card.position}号阵营）这一局的记忆——按时间顺序，含我收到的情报、" +
                      "对手说过的话、我自己的内心独白与行动】");
        sb.AppendLine(transcript);
        sb.AppendLine();
        sb.AppendLine("请按要求的五行格式，写出我的长期记忆。");
        return sb.ToString();
    }

    // ==================== 素材 ====================

    /// <summary>把这张卡的 history 摊成一份"它的记忆"文本：跳过 system（人设不是经历）。</summary>
    private static string BuildTranscript(CharacterCard card)
    {
        List<DeepSeekMessage> history = card != null ? card.history : null;
        if (history == null || history.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            DeepSeekMessage m = history[i];
            if (m == null || string.IsNullOrWhiteSpace(m.content)) continue;
            if (m.role == "system") continue;

            string who;
            switch (m.role)
            {
                case "assistant": who = "我"; break;
                case "user": who = "我收到的"; break;
                case "tool": who = "工具结果"; break;
                default: who = m.role; break;
            }

            sb.Append('[').Append(who).Append("] ").Append(m.content.Trim()).Append('\n');

            if (m.tool_calls != null && m.tool_calls.Count > 0)
            {
                var names = new List<string>();
                foreach (ToolCall tc in m.tool_calls)
                    if (tc != null && tc.function != null && !string.IsNullOrEmpty(tc.function.name))
                        names.Add(tc.function.name);
                if (names.Count > 0)
                    sb.Append("（这一轮我调用了工具：").Append(string.Join("、", names)).Append("）\n");
            }
        }

        string text = sb.ToString().Trim();
        if (text.Length <= TranscriptCharBudget) return text;

        // 太长：留最前面（开局结盟、试探）和最后面（怎么死的），中间的经历掐掉
        int head = (int)(TranscriptCharBudget * 0.4f);
        int tail = TranscriptCharBudget - head;
        return text.Substring(0, head) + "\n……（中间的经历略）……\n" + text.Substring(text.Length - tail);
    }

    /// <summary>这一局对这个阵营是什么结果（写在记忆文件里做抬头，也给模型当"结局"）。</summary>
    private static string DescribeOutcome(CharacterCard card, int winnerStage)
    {
        int stage = card.position;
        string winnerText = winnerStage > 0
            ? $"，这一局是 {AIAgent.GetStageName(winnerStage)}（{winnerStage}号阵营）赢的"
            : "";

        if (winnerStage > 0 && winnerStage == stage)
            return $"{card.name}（{stage}号阵营）占满全图，赢下这一局";
        if (AIAgent.IsStageEliminated(stage))
            return $"{card.name}（{stage}号阵营）被击杀出局{winnerText}";
        return $"{card.name}（{stage}号阵营）活到了最后但没赢{winnerText}";
    }

    /// <summary>
    /// 全体共享的本局对局情况：谁在第几轮被谁用什么杀掉、最后谁赢了、终局各家占地多少。
    /// 全部来自客观数据（AIAgent 报的击杀 + 领地扫描），四个阵营的文件里写的是同一份，不经过模型。
    /// </summary>
    private static string BuildSharedSituation(int winnerStage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【全体共享·本局对局情况】（四个阵营看到的是同一份客观事实）");

        if (deaths.Count == 0)
        {
            sb.AppendLine("- 本局没有人被击杀");
        }
        else
        {
            foreach (DeathRecord d in deaths)
            {
                string victim = $"{d.stage}号阵营({AIAgent.GetStageName(d.stage)})";
                if (d.killerStage > 0)
                {
                    string killer = $"{d.killerStage}号阵营({AIAgent.GetStageName(d.killerStage)})";
                    string weapon = d.weapon.Length > 0 ? $"用{d.weapon}" : "";
                    sb.AppendLine($"- 第 {d.round} 轮：{victim} 被 {killer}{weapon}击杀，出局");
                }
                else
                {
                    sb.AppendLine($"- 第 {d.round} 轮：{victim} 出局（没有明确的凶手）");
                }
            }
        }

        int[] area = InformGetter.GetTerritoryArea();
        int total = 0;
        if (area != null)
            for (int i = 0; i < area.Length; i++) total += area[i];

        if (winnerStage > 0)
        {
            string percent = area != null && total > 0 && winnerStage < area.Length
                ? $"{area[winnerStage] * 100f / total:0.#}%"
                : "未知";
            sb.AppendLine($"- 获胜：{winnerStage}号阵营({AIAgent.GetStageName(winnerStage)})，终局占地 {percent}");
        }
        else
        {
            sb.AppendLine("- 本局没有产生获胜者（手动终止）");
        }

        if (area != null && total > 0)
        {
            var parts = new List<string>();
            for (int stage = 1; stage < area.Length; stage++)
                parts.Add($"{stage}号({AIAgent.GetStageName(stage)}) {area[stage] * 100f / total:0.#}%");
            sb.AppendLine("- 终局各家占地：" + string.Join("、", parts));
        }

        sb.Append($"- 全局共进行了 {Mathf.Max(1, AIAgent.CurrentRound)} 轮");
        return sb.ToString();
    }

    // ==================== 写盘 ====================

    private static void AppendToFile(CharacterCard card, string outcome, string memory, string shared)
    {
        try
        {
            string path = FilePathFor(card);
            Directory.CreateDirectory(FolderPath);

            var sb = new StringBuilder();
            if (!File.Exists(path))
                sb.AppendLine($"# {card.name}（{card.position}号阵营）的长期记忆（每局追加一段）");
            sb.AppendLine();
            sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm} =====");
            sb.AppendLine($"结局：{outcome}");
            sb.AppendLine(memory.Trim());
            sb.AppendLine();
            sb.AppendLine(shared);
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Debug.LogError($"[终局记忆] 写记忆文件失败（{FilePathFor(card)}）：{e}");
        }
    }

    private static string LoadApiKey()
    {
        try
        {
            string path = Path.Combine(Application.persistentDataPath, "Key.txt");
            if (!File.Exists(path))
            {
                Debug.LogError($"[终局记忆] Key.txt not found at {path}");
                return "";
            }
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            Debug.LogError($"[终局记忆] 读 Key.txt 失败：{e}");
            return "";
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        return sb.ToString().Trim();
    }
}
