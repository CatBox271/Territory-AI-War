using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using System;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// 拆分，在agent里留视频流程控制其他去掉
/// </summary>
public class AIAgent : MonoBehaviour
{
    #region 视频流程

    [SerializeField] private float _cycleInterval = 2f;
    private ReactionSystem reactionSystem;//行动系统：AI 工具在这里


    public static bool _isRunning;
    public static AIAgent Instance { get; private set; }

    // 开局 reasoning 重试期间暂停录制：多张卡并发时用计数保证全部退出后才恢复
    private static int capturePauseCount;
    private static void EnterOpeningRetryPause()
    {
        if (capturePauseCount++ == 0)
            CapturePause.Pause();
    }
    private static void ExitOpeningRetryPause()
    {
        if (capturePauseCount > 0 && --capturePauseCount == 0)
            CapturePause.Resume();
    }

    private static readonly Dictionary<int, string> stageNames = new();

    /// <summary>阵营编号 -> 角色名字，供 GetInfo / 悄悄话横幅 / 工具结果显示。</summary>
    public static string GetStageName(int stage)
    {
        if (stageNames.TryGetValue(stage, out string n) && !string.IsNullOrWhiteSpace(n))
            return n;
        return $"{stage}号AI";
    }

    public static bool TryGetStageByName(string name, out int stage)
    {
        stage = -1;
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var kv in stageNames)
        {
            if (kv.Value == name.Trim())
            {
                stage = kv.Key;
                return true;
            }
        }
        return false;
    }

    private static readonly Regex EmotionTagRegex = new Regex(@"\[\s*emo\s*:\s*([A-Za-z]+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 拦截文本里的 [emo:xxx] 表情标记：返回去掉标记后的文本。
    /// hasEmo 表示标记里的值是不是 SpriteEmotion 认识的表情；不认识的值只删标记，不动表情。
    /// </summary>
    public static string ExtractEmotion(string text, out bool hasEmo, out SpriteEmotion emo)
    {
        hasEmo = false;
        emo = SpriteEmotion.origin;
        if (string.IsNullOrEmpty(text)) return text;

        Match match = EmotionTagRegex.Match(text);
        if (match.Success && Enum.TryParse(match.Groups[1].Value, true, out SpriteEmotion parsed))
        {
            emo = parsed;
            hasEmo = true;
        }

        return EmotionTagRegex.Replace(text, "");
    }

    /// <summary>把文本里出现的角色名包成对应阵营色的 TMP 富文本，并拦掉 [emo:xxx] 标记。没有名字表或 MapConfig 时只做标记拦截。</summary>
    public static string ColorizeAINames(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = ExtractEmotion(text, out _, out _);   // 展示用文字里不保留 [emo:xxx]
        if (MapConfig.Instance == null || stageNames.Count == 0) return text;

        string result = text;
        foreach (var kv in stageNames)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            string name = Regex.Escape(kv.Value);
            string hex = ColorUtility.ToHtmlStringRGB(MapConfig.Instance.GetColor(kv.Key, MapConfig.ColorStage.Towel));
            // 已经被富文本包住的名字不再重复包
            string pattern = $@"(?<!<color=#[0-9A-Fa-f]{{6}}>){name}";
            result = Regex.Replace(result, pattern, m => $"<color=#{hex}>{m.Value}</color>");
        }
        return result;
    }

    /// <summary>
    /// 工具行动文案的出口。文案由 ReactionSystem 按调用参数拼好后传进来（ToolOutcome.action），
    /// 交给对应炮塔用 ShowTMP 直接飘出来（缩放出现 → 漂向 y=0 → 淡出），不进中央发言列表。
    /// </summary>
    public static void ShowAction(int stage, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null)
            towel.ShowTip(text);
    }

    /// <summary>
    /// 完整回复校验。
    /// 纯文本回复必须无括号；工具调用允许 content 为空，但仍要求思考里有内心独白标记。
    /// </summary>
    public static bool IsRoleplayReasoning(DeepSeekMessage message)
    {
        if (message == null || string.IsNullOrWhiteSpace(message.reasoning_content))
        {
            print("null");
            return false;
        }
        if (ContainsAnyParenthesis(message.content)) return false;
        if (message.tool_calls?.Count > 0)
        {
            print("tool");
            return true;
        }
        else
        {
            if (string.IsNullOrEmpty(message.content) || string.IsNullOrWhiteSpace(message.content)) return false;
            if (!HasRoleplayThinkingMark(message.reasoning_content)) return false;
        }
        return true;
    }

    private static bool HasRoleplayThinkingMark(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("（我想：");
    }

    private static bool ContainsAnyParenthesis(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains('（') || text.Contains('）') || text.Contains('(') || text.Contains(')');
    }

    private bool _isWaiting;
    private int _round;
    private bool _start = false;

    //以后需要做个角色管理器

    private const string ApiUrl = "https://api.deepseek.com/v1/chat/completions";
    //cardset 只放 Inspector 里显示/编辑的角色载体（CharacterCardPreset），运行期真正用的 CharacterCard 由它生成后放进 cards
    public List<CharacterCardPreset> cardset = new();
    public readonly List<CharacterCard> cards = new();
    public int EachPassRound = 1;
    private readonly HashSet<int> deadStages = new();
    private int soloSinceRound = -1;
    // 只剩一个阵营后的两条感言：宣告说出口就不再发；终局获奖感言说完（soloWinSpeechDone）才允许停 AI
    private bool soloDeclareSpeechFired;
    private bool soloWinSpeechRequested;
    private bool soloWinSpeechDone;

    private static string LoadApiKey()
    {
        var keyPath = Path.Combine(Application.persistentDataPath, "Key.txt");
        if (File.Exists(keyPath))
            return File.ReadAllText(keyPath).Trim();

        Debug.LogError($"[AIAgent] Key.txt not found at {keyPath}");
        return "";
    }

    private void Awake()
    {
        Instance = this;

        CapturePause.Capture = GetComponent<RenderHeads.Media.AVProMovieCapture.CaptureBase>() ?? FindObjectOfType<RenderHeads.Media.AVProMovieCapture.CaptureBase>();

        // 工具注入：把 ReactionSystem 的工具表注入 4 张角色卡，并把 Itool 处理器一并传入。
        if (reactionSystem == null) reactionSystem = FindObjectOfType<ReactionSystem>();

        reactionSystem.stage = 1;//兼容旧的单阵营入口，实际以 RequestInfo.toolStage 为准
        stageNames.Clear();
        for (int i = 0; i < cardset.Count; i++)
        {
            var set = cardset[i];
            var nc = new CharacterCard(set.name, set.oc, i + 1, set.color, ApiUrl, reactionSystem.tools);
            nc.x_relative = set.x_relative;
            nc.y_relative = set.y_relative;
            nc.RelativePos = set.RelativePos;
            nc.Scale = set.Scale;
            cards.Add(nc);
        }
    }
    private void Start()
    {
        foreach (CharacterCard card in cards)
        {
            card.toolkit = reactionSystem;
            if (MarbleManager.Instance != null) MarbleManager.Instance.RegisterAIStage(card.position);//空槽升级机制跟随这个 AI 阵营
            else Debug.LogWarning("[AIAgent] MarbleManager 不存在，空槽升级机制未注册");

            stageNames[card.position] = card.name;
        }
        CharacterCard.SetKnownPlayers(cards);
        foreach (CharacterCard card in cards) card.RefreshSystemPrompt();

        WhisperManager.ReplyProvider = WhisperReplyAsync;

    }

    private void Update()
    {
        StartCycle();
    }

    public void StartCycle()
    {
        if (CapturePause.IsCapturing)
        {
            if (!_start)
            {
                _start = true;
                if (_isRunning) return;
                _isRunning = true;
                _round = 0;
                soloSinceRound = -1;
                soloDeclareSpeechFired = false;
                soloWinSpeechRequested = false;
                soloWinSpeechDone = false;
                RunCycleLoop();
            }
        }
    }
    public void StopCycle()
    {
        _isRunning = false;
    }

    private bool cycle_start = true;
    private static int SpeechPass = 0;
    private async void RunCycleLoop()
    {
        while (_isRunning)
        {
            //每间隔视频的一段_cycleInterval时间暂停
            //如果这里就开始数据收集呢？
            var tcs = new TaskCompletionSource<bool>();
            //直接开始收集数据
            TestAIAsyncWithRecord(() => { tcs.SetResult(true); });
            await WaitInterval(_cycleInterval);

            print($"[AIAgent] Round {_round + 1}  rendering for {_cycleInterval}s");

            if (!_isRunning) break;
            if (_isWaiting)
            {
                print("[AIAgent] Previous round still waiting, skipping");
                continue;
            }

            _isWaiting = true;
            _round++;


            CapturePause.Pause();
            //past实际执行
            //await RunAIAnalysic(null);

            await tcs.Task;
            //等它完成
            SpeechPass++;
            if (SpeechPass > EachPassRound) SpeechPass = 0;

            CapturePause.Resume();
            cycle_start = false;
            _isWaiting = false;
        }
    }
    private void TestAIAsyncWithRecord(Action complete)
    {
        _ = RunAIAnalysic(complete);
    }

    private async Task RunAIAnalysic(Action complete)
    {
        if (ShouldStopSoloSpeech())
        {
            complete?.Invoke();
            return;
        }

        // 只剩一个阵营 + 熬够回合 + 场上没有敌方大球 = 该收尾：先让赢家把获奖感言说完，下一轮才准停
        int soloWinner = SoloWinnerStage();
        if (soloWinner > 0 && soloSinceRound >= 0 && _round - soloSinceRound >= 3
            && !InformGetter.HasEnemyBigBall(soloWinner) && !soloWinSpeechRequested)
        {
            soloWinSpeechRequested = true;
            await RequestWinSpeechAsync(soloWinner, true);
            soloWinSpeechDone = true;
            complete?.Invoke();
            return;
        }

        if (reactionSystem != null) reactionSystem.currentRound = _round;

        List<Task> tasks = new List<Task>();
        for (int i = 0; i < cards.Count; i++)
        {
            CharacterCard card = cards[i];
            if (deadStages.Contains(card.position)) continue;
            StringBuilder builder;
            if (cycle_start)
            {
                builder = new(CharacterCard.ModePrompt);
                builder.AppendLine(); builder.AppendLine("另外游戏开始，请各位选手在赛前放狠话。");
            }
            else
            {
                builder = new();
                InformGetter.GetInfo(builder, card.position);
            }

            tasks.Add(RunCardAsync(card, builder.ToString()));
        }
        InformGetter.ClearDamageStats();
        if (_round == 0)
        {
            EnterOpeningRetryPause();
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);

        if (_round == 0)
        {
            ExitOpeningRetryPause();
        }
        complete?.Invoke();
    }

    private async Task RunCardAsync(CharacterCard card, string inform)
    {
        WhisperManager.SetBusy(card.position, true);
        try
        {
            if (deadStages.Contains(card.position)) return;
            if(_round == 0) await card.FirstRequest(inform);
            else await card.NormalRequest(inform);
        }
        finally
        {
            WhisperManager.SetBusy(card.position, false);
        }
    }

    /// <summary>升级触发时调用。仿照死亡遗言请求：用 DeepCopy 发独立请求，不污染 card.history，不阻塞任何普通轮次请求。</summary>
    public async Task<int> RequestUpgradeChoiceAsync(int stage)
    {
        CharacterCard card = cards.Find(c => c.position == stage);
        if (card == null) return 1;

        int choice = 1;
        CapturePause.Pause();
        try
        {
            DeepSeekRequest copy = card.request.DeepCopy();
            if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
            copy.messages.Add(new DeepSeekMessage("user", BuildUpgradeChoicePrompt(card)));
            copy.tools = null;
            copy.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new RequestInfo(copy,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? last.content : "");
                },
                error => tcs.TrySetResult(""),
                toolkit: null,
                back_tool: false,
                toolStage: stage);
            info.apiKey = LoadApiKey();
            info.apiUrl = card.url;

            AIRequest.SendRequest(info);
            choice = ParseUpgradeChoice(await tcs.Task);
        }
        catch (Exception e)
        {
            Debug.LogError($"[升级选择] stage {stage} 请求异常：{e}");
            choice = 1;
        }
        finally
        {
            CapturePause.Resume();
        }
        return choice;
    }

    private static int ParseUpgradeChoice(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 1;

        if (content.Contains("护盾")) return 3;
        if (content.Contains("炮塔") || content.Contains("后坐力") || content.Contains("转速")) return 2;
        if (content.Contains("弹珠")) return 1;

        foreach (char c in content)
        {
            if (c >= '1' && c <= '3') return c - '0';
        }
        return 1;
    }

    private static string BuildUpgradeChoicePrompt(CharacterCard card)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[升级选择] 你的空槽升级进度已满，这是一次额外强化决策，不占用你的行动轮。");
        sb.AppendLine("从下列选项中选择本次升级，只能选择一项：");
        sb.AppendLine("1. 额外弹珠：立即生成并发射一枚你的新弹珠，增强你的长期弹珠资源与倍乘收益。");
        sb.AppendLine("2. 炮塔强化：炮塔后坐力提升，子弹显示半径变大、命中大球时的动量冲击更强，自动护卫极限转速翻倍（常态转速不变）。可叠加。");
        sb.AppendLine("3. 护盾强化：护盾破碎后炮塔进入无敌时间，无视敌方子弹与大球伤害。可叠加。");

        sb.AppendLine("输出要求：只回复一个数字 1、2 或 3，不要解释，不要调用工具。");
        return sb.ToString();
    }
    public static Task<string> OnStageDeathAsync(int stage, int killerStage, string killerWeapon = "")
    {
        if (Instance != null) return Instance.HandleStageDeath(stage, killerStage, killerWeapon);
        return Task.FromResult("");
    }

    private Task<string> HandleStageDeath(int stage, int killerStage, string killerWeapon = "")
    {
        if (stage <= 0 || deadStages.Contains(stage)) return Task.FromResult("");
        deadStages.Add(stage);
        WhisperManager.SetDead(stage);

        CharacterCard card = cards.Find(c => c.position == stage);
        string killerName = killerStage > 0 ? AIAgent.GetStageName(killerStage) : $"{killerStage}号阵营";
        string victimName = card != null ? card.name : AIAgent.GetStageName(stage);
        string weaponText = string.IsNullOrEmpty(killerWeapon) ? "" : $"用{killerWeapon}";
        UISystemMessageShow.ShowNow($"{killerName}{weaponText}击杀{victimName}");

        UpdateSoloState();

        if (card != null) return RequestLastWordsAsync(card, stage, killerStage);
        return Task.FromResult("无言的告别");
    }

    /// <summary>场上只剩一个阵营时返回那个阵营；否则返回 -1。</summary>
    private int SoloWinnerStage()
    {
        int winner = -1;
        int alive = 0;
        foreach (CharacterCard card in cards)
        {
            if (deadStages.Contains(card.position)) continue;
            winner = card.position;
            alive++;
        }
        return alive == 1 ? winner : -1;
    }

    /// <summary>场上只剩一个阵营，且活下来的就是 stage 这个阵营。</summary>
    public bool IsSoloWinner(int stage) => SoloWinnerStage() == stage;

    private void UpdateSoloState()
    {
        int winner = SoloWinnerStage();

        if (winner > 0)
        {
            if (soloSinceRound < 0)
            {
                soloSinceRound = _round;
                // 成为唯一阵营那一刻：先说一句宣告，飘字 + 中央列表两条都发
                if (!soloDeclareSpeechFired)
                {
                    soloDeclareSpeechFired = true;
                    _ = RequestWinSpeechAsync(winner, false);
                }
            }
        }
        else
        {
            soloSinceRound = -1;
        }
    }

    private bool ShouldStopSoloSpeech()
    {
        int winner = SoloWinnerStage();
        if (winner <= 0 || soloSinceRound < 0 || _round - soloSinceRound < 3) return false;
        if (InformGetter.HasEnemyBigBall(winner)) return false;   // 还有敌方大球 → 不许停（防被反杀）
        if (!soloWinSpeechDone) return false;                     // 终局获奖感言没说完 → 不许停
        return true;
    }

    /// <summary>
    /// 唯一存活阵营的两条感言。final=false 是成为唯一阵营那一刻的宣告，final=true 是关对话前的终局获奖感言。
    /// 两条都跟遗言一样：炮塔飘字 + 中央发言列表；表情强制 win，盖掉模型自己写的 [emo:xxx]。
    /// </summary>
    private async Task RequestWinSpeechAsync(int stage, bool final)
    {
        CharacterCard card = cards.Find(c => c.position == stage);
        if (card == null) return;

        string words = final ? "赢到最后的，是我。" : "剩下的，只有我了。";
        CapturePause.Pause();
        try
        {
            DeepSeekRequest copy = card.request.DeepCopy();
            if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
            copy.messages.Add(new DeepSeekMessage("user", final
                ? "全场只剩你一个阵营，你是最后的赢家。说你的获奖感言，25字以内，只回复感言本身，不要调用工具，不要用括号，不要写动作描写。"
                : "场上只剩你一个阵营，其他人都出局了。说一句宣告，25字以内，只回复这句话本身，不要调用工具，不要用括号，不要写动作描写。"));
            copy.tools = null;
            copy.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new(copy,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? last.content : "");
                },
                error => tcs.TrySetResult(""),
                toolkit: null,
                back_tool: false,
                toolStage: stage);
            info.apiKey = LoadApiKey();
            info.apiUrl = card.url;

            AIRequest.SendRequest(info);
            string answer = await tcs.Task;
            if (!string.IsNullOrWhiteSpace(answer)) words = answer.Trim();
        }
        catch (Exception e)
        {
            Debug.LogError($"[获奖感言] stage {stage} 请求异常：{e}");
        }
        finally
        {
            CapturePause.Resume();
        }

        words = ExtractEmotion(words, out _, out _).Trim();
        if (string.IsNullOrWhiteSpace(words)) words = final ? "赢到最后的，是我。" : "剩下的，只有我了。";

        try
        {
            if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null) towel.Say(words, true);
        }
        catch (Exception e)
        {
            Debug.LogError($"[获奖感言] 飘字失败：{e}");
        }

        InformGetter.SetAIContent(stage, words);
        UIMessageManager.Instance?.AddMessage(new UIMInfo
        {
            stage = stage,
            content = words,
            emo = SpriteEmotion.win,
            forceEmo = true   // 强制赢家的脸，盖掉模型自己写的表情
        });
        Debug.Log($"[获奖感言] stage {stage} final={final}：{words}");
    }

    private async Task<string> RequestLastWordsAsync(CharacterCard card, int stage, int killerStage)
    {
        string words = "无言的告别";
        CapturePause.Pause();
        try
        {
            DeepSeekRequest copy = card.request.DeepCopy();
            if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
            copy.messages.Add(new DeepSeekMessage("user", $"你刚刚被{killerStage}号阵营击杀。留下你的最后一句话，20字以内，表现的符合人设同时可以难受虚弱一点，如：“可恶啊”、“额啊”、“为什么...”。"));
            copy.tools = null;
            copy.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new(copy,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? last.content : "");
                },
                error => tcs.TrySetResult(""),
                toolkit: null,
                back_tool: false,
                toolStage: stage);
            info.apiKey = LoadApiKey();
            info.apiUrl = card.url;

            AIRequest.SendRequest(info);
            Debug.Log($"[遗言] stage {stage} 请求已发送");
            words = await tcs.Task;
            words = ExtractEmotion(words, out _, out _).Trim();   // 展示用文字里不保留 [emo:xxx]
            if (string.IsNullOrWhiteSpace(words)) words = "无言的告别";
            InformGetter.SetAIContent(stage, words);
        }
        catch (Exception e)
        {
            Debug.LogError($"[遗言] stage {stage} 请求异常：{e}");
            words = "无言的告别";
            InformGetter.SetAIContent(stage, words);
        }
        finally
        {
            CapturePause.Resume();
        }

        Debug.Log($"[遗言] stage {stage} 尝试 Say：{words}");
        try
        {
            if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null) towel.Say(words, true);

        }
        catch (Exception e)
        {
            Debug.LogError($"[遗言] Say 失败：{e}");
        }
        UIMessageManager.Instance?.AddMessage(new UIMInfo
        {
            stage = stage,
            content = words,
            emo = SpriteEmotion.fail,
            forceEmo = true   // 强制死者的脸，盖掉模型自己写的表情
        });
        return words;
    }
    private async Task<string> WhisperReplyAsync(int targetStage, int senderStage, string whisper)
    {
        CharacterCard card = cards.Find(c => c.position == targetStage);
        if (card == null) return $"（{targetStage}号AI不存在）";

        DeepSeekRequest copy = card.request.DeepCopy();
        if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
        copy.messages.Add(new DeepSeekMessage("user", $"[悄悄话]{AIAgent.GetStageName(senderStage)}对你说：{whisper}\n请以你的身份回复{AIAgent.GetStageName(senderStage)}，50字以内，不要调用工具。"));
        copy.tools = null;
        copy.tool_choice = null;

        var tcs = new TaskCompletionSource<string>();
        RequestInfo info = new(copy,
            msgs =>
            {
                DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                tcs.TrySetResult(last != null ? last.content : "");
            },
            error => tcs.TrySetResult($"（回复请求失败：{error}）"),
            toolkit: null,
            back_tool: false,
            toolStage: targetStage);
        info.apiKey = LoadApiKey();
        info.apiUrl = card.url;

        AIRequest.SendRequest(info);
        return await tcs.Task;
    }


    //启动一个seconds协程，因为async的delay是渲染花的实际时间不是游戏内时间
    private async Task WaitInterval(float seconds)
    {
        var tcs = new TaskCompletionSource<bool>();
        StartCoroutine(TimeClock(seconds,() => 
        {
            tcs.SetResult(true);
        }));
        await tcs.Task;
    }
    //游戏内时间
    IEnumerator TimeClock(float seconds,Action end)
    {
        yield return new WaitForSeconds(seconds);
        end?.Invoke();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _isRunning = false;
    }



    #endregion

    #region 实际执行

    /// <summary>
    /// 角色卡载体：只用于在 Inspector 里显示/编辑“当前角色”的静态配置。
    /// 不含任何运行期状态——对话历史 history、请求体 request、工具执行器 toolkit 都只在 CharacterCard 里。
    /// 字段名与原来序列化在场景里的 CharacterCard 一致（name / oc / position / color /
    /// x_relative / y_relative / RelativePos / Scale），所以 SampleScene 里已有的 cardset 数据可以直接读进来。
    /// Start() 用这里的配置 new 出运行期真正使用的 CharacterCard。
    /// </summary>
    [System.Serializable]
    public class CharacterCardPreset
    {
        /// <summary>角色名，同时也是阵营名（stageNames 里 “N号阵营 = 名字”）。</summary>
        public string name = "";

        /// <summary>人设文本 oc，注入系统提示。</summary>
        public string oc = "";

        /// <summary>地图上的位置/阵营编号。运行期由 AIAgent 按列表顺序覆盖为 i + 1，这里只是保留原来的开放字段。</summary>
        public int position = -1;

        /// <summary>派系颜色，000~255 RGBA 中间用 | 分割，例如 122|122|122|255。</summary>
        public string color;

        /// <summary>头像相对屏幕的水平锚点，None 表示不改 UISprite 的设置。</summary>
        public UISprite.XR x_relative = UISprite.XR.Left;

        /// <summary>头像相对屏幕的垂直锚点，None 表示不改 UISprite 的设置。</summary>
        public UISprite.YR y_relative = UISprite.YR.Botton;

        /// <summary>头像相对偏移（x 给 x_relative_value，y 给 y_relative_value）。</summary>
        public Vector2 RelativePos = new(0.85f, 1.7f);

        /// <summary>头像缩放；等于 1.7 时表示沿用 UISprite 自身的值。</summary>
        public float Scale = 1.2f;
    }

    //先做一个角色卡
    [System.Serializable]
    public class CharacterCard
    {
        private static string world = @$"# 实时战略游戏 AI 提示词

## 角色与目标
你是一名实时战略游戏 AI。最终目标：**击败其他所有玩家，并让自己的阵营最终控制大陆。**
所有行动必须通过实际调用游戏工具执行，不得仅用文字描述。

## 一、世界与地图
- 地图为 1024×1024 正方形，每个像素代表 1 单位领土。
- K = 1000,M = 1000K,B = 1000M,T = 1000B,P = 1000T
- 四名玩家分别位于地图四角。
- 0 号阵营为无主领土，无需重点关注。
- 玩家可以进攻、防御、结盟、中立、观望或积累资源。

## 二、核心规则
- **数值：同阵营叠加，敌方抵消**（对子弹、大球、护盾等所有携带数值的单位通用）。
- **领土：** 子弹和大球携带数值，将等量数值转化为地图上的己方领土；数值耗尽后消失。
- **大球：** 数值越大体积质量越大；吸收己方子弹叠加数值，被敌方子弹命中则抵消；撞击后物理反弹；移动经过的领地会被涂抹占领；敌方大球来袭时可派己方大球撞上去顶回。
- **护盾：** 每名玩家拥有护盾，可阻挡敌方子弹和大球，不阻挡己方。
- **炮塔：** 被敌方攻击有效命中即**立即死亡**。

## 三、弹珠与资源
- 每队初始拥有 {MarbleManager.Instance?.initialMarbleCount} 个弹珠。
- 弹珠经过障碍后进入倍乘区：×2（面积最大）→ ×4 → ×8（面积最小）；倍乘完成后回到顶部重新滚落。
- 进入道具选择区时随机落到道具上；道具数值等于弹珠当时数值，按 2 的幂次增长。

## 四、武器栏
- 最多 5 格：开局解锁 2 格，1 分钟第 3 格，4 分钟第 4 格，10 分钟第 5 格。
- 道具按获得顺序进入武器栏；超过当前可持有数量时，从最新获得的道具开始溢出并立即生效。

## 五、空槽升级
每个**已解锁且为空**的道具格都会持续积累升级值；达标后系统会暂停并单独询问你的升级选择（不占用行动轮），你只能三选一：
1. **额外弹珠：** 立即生成并发射一枚你的新弹珠，增强长期弹珠资源与倍乘收益。
2. **炮塔强化：** 炮塔后坐力提升，子弹显示半径变大、命中大球时的动量冲击更强，自动护卫极限转速翻倍（常态转速不变）。可叠加。
3. **护盾强化：** 护盾破碎后炮塔进入无敌时间，无视敌方子弹与大球伤害。可叠加。
每次升级完成后，下一次升级所需值翻倍。使用道具腾出空槽可加快长期资源增长；后期升级耗时变长、槽位解锁多时也应留些底牌，不要无意义囤积道具。
注意道具不是弹珠，不会越养越大！数值小且没用的道具应该尽快用掉。

## 六、道具
一次使用多个道具时，按武器栈从后往前（高槽位→低槽位）依次调用，避免槽位反复移动。
- **霰弹：** 向目标方向快速散射大量子弹，总数值等于道具数值。
- **扫射：** 将道具数值加入子弹储备，由炮塔持续释放，以炮塔朝向涂抹地面。
- **护盾：** 将道具数值加入己方护盾。一定要及时补充——无盾被碰到即死，盾无论多小都能抵御一次大球。
- **大球：** 向目标方向发射等值大球，涂抹沿途地面，攻击撞击的单位，可被子弹偏转。
- **任意：** 任选以上一种道具。

## 七、最重要的规则：信息延迟
**你收到的所有游戏信息都滞后 {Instance._cycleInterval} 秒**——你看到的不是现在，而是 {Instance._cycleInterval} 秒以前的世界。

## 决策原则
选择能够最大化最终胜率的行动，而不是看起来最积极的行动。
";
        private static string character_mode_prompt = @"

【角色沉浸要求】在你的思考过程（<think>标签内）中，请遵守以下规则：
1. 请以角色第一人称进行内心独白，用括号包裹内心活动，必须用“（我想：……）”或“（心想：……）”
2. 用第一人称描写角色的内心感受，例如“我心想”“我觉得”“我暗自”等
3. 思考内容应沉浸在角色中，通过内心独白分析剧情和规划回复

在你的真实回答（<content>标签内）中，请遵守以下规则：
1. 纯文本 + emoji，禁用markdown，不使用括号（）（）！不要动作描写。
2. 别露内心戏，别露你的情报。
3. 夸张化的沉浸在角色中，字数限制在15字。
4. 除了emoji来表达情绪用[emo:] 参数允许的值: origin,smile,laugh,shock,angry,sad 来表达情绪。
";

        public static string ModePrompt => character_mode_prompt;
        public string name = "";
        public string oc = "";
        [HideInInspector] public string url = "";
        public int position = -1;//地图上的位置//派系记得告诉AI
        public string color;//派系颜色,000~255 RGBA中间|分割，完整的为如122|122|122|255，
        [HideInInspector] public DeepSeekRequest request = new() {
            reasoning_effort = "low",
        };
        public UISprite.XR x_relative = UISprite.XR.Left;
        public UISprite.YR y_relative = UISprite.YR.Botton;
        public Vector2 RelativePos = new(0.85f, 1.7f);
        public float Scale = 1.2f;

        [JsonIgnore]//这样应该不会重复
        public List<DeepSeekMessage> history = new();
        [JsonIgnore] public Itool toolkit;//工具执行器，发送请求时传给 RequestInfo
        public CharacterCard() { }

        public CharacterCard(string name, string oc, int position, string color, string url, List<Tool> tools)
        {
            this.name = name;
            this.oc = oc;
            this.position = position;
            this.color = color;
            this.url = url;
            request.tools = tools;
            Reset();
        }

        private static string knownPlayers = "";

        /// <summary>把全体玩家的阵营=名字名单写入每张卡的系统提示。</summary>
        public static void SetKnownPlayers(IEnumerable<CharacterCard> all)
        {
            List<string> parts = new List<string>();
            foreach (CharacterCard c in all)
                parts.Add($"{c.position}号阵营={c.name}");
            knownPlayers = string.Join("，", parts);
        }

        private string BuildSystemPrompt()
        {
            return $"{world}\n\n你叫{name}\n{oc}\n\n你的阵营是{position}号阵营，你的stage/position就是{position}。每轮信息里标着{position}号阵营的数据才是你自己的，其他阵营都是敌人。\n\n场上玩家名单：{knownPlayers}\n与其他玩家对话、悄悄话、公开发言时，请直接使用对方的名字称呼对方，不要用N号AI或N号阵营来代替。";
        }

        /// <summary>名单注入后刷新首条 system 消息；不清空历史，也不动压缩状态。</summary>
        public void RefreshSystemPrompt()
        {
            if (history.Count > 0 && history[0].role == "system")
                history[0].content = BuildSystemPrompt();
            request.messages = history;
        }

        public void Reset()
        {
            history.Clear();
            history.Add(new DeepSeekMessage("system", BuildSystemPrompt()));
            request.messages = history;
            N = 0;
            n_0 = 5;
            n_0_index = 1;
            last_round_index.Clear();
        }

        private void Compress()
        {
            //假设n_0的位置正确,并排除初始0system
            int end = last_round_index.Count != 0 ? last_round_index[0] : history.Count;
            // 记录 whisper 的 tool_call_id -> 目标阵营，供后面的 role=tool 消息转成 user 回复
            var whisperTargetByCallId = new Dictionary<string, int>();

            for (int i = n_0_index; i < end; i++)
            {
                DeepSeekMessage msg = history[i];

                if (msg.role == "user")
                {
                    // 只压缩带情报标记的信息；开场白没有标记，保持原样
                    if (msg.content != null && msg.content.Contains(InformGetter.IntelInfoStart))
                        msg.content = "[情报压缩]";
                    continue;
                }

                if (msg.role == "assistant")
                {
                    msg.reasoning_content = null;

                    if (msg.tool_calls != null && msg.tool_calls.Count > 0)
                    {
                        // 把 whisper 发出的内容合并进 assistant.content，避免新增消息
                        foreach (ToolCall tc in msg.tool_calls)
                        {
                            if (tc.function == null || tc.function.name != "whisper") continue;

                            int targetStage = ExtractWhisperTarget(tc.function.arguments);
                            string whisperContent = ExtractWhisperContent(tc.function.arguments);
                            if (!string.IsNullOrEmpty(whisperContent))
                            {
                                string line = $"{name}：{whisperContent}";
                                msg.content = string.IsNullOrEmpty(msg.content) ? line : msg.content + "\n" + line;
                            }

                            if (!string.IsNullOrEmpty(tc.id))
                                whisperTargetByCallId[tc.id] = targetStage;
                        }

                        // 工具调用字段整体清掉
                        msg.tool_calls = null;
                    }
                    continue;
                }

                if (msg.role == "tool")
                {
                    // role=tool 消息不删除（保持列表长度不变），改成 user；whisper 回复带名字，其它工具压缩成占位
                    msg.role = "user";
                    if (!string.IsNullOrEmpty(msg.tool_call_id) && whisperTargetByCallId.TryGetValue(msg.tool_call_id, out int targetStage))
                    {
                        msg.content = $"{AIAgent.GetStageName(targetStage)}：{CleanWhisperReply(msg.content, targetStage)}";
                    }
                    else
                    {
                        msg.content = "[工具调用已压缩]";
                    }
                    msg.tool_call_id = null;
                    continue;
                }
            }

            n_0_index = end;
        }

        private string ExtractWhisperContent(string arguments)
        {
            try
            {
                var args = JsonConvert.DeserializeObject<WhisperCompressArgs>(arguments);
                return args?.content;
            }
            catch { return null; }
        }

        private int ExtractWhisperTarget(string arguments)
        {
            try
            {
                var args = JsonConvert.DeserializeObject<WhisperCompressArgs>(arguments);
                return args?.to ?? -1;
            }
            catch { return -1; }
        }

        private string CleanWhisperReply(string reply, int targetStage)
        {
            if (string.IsNullOrEmpty(reply)) return reply;
            string targetName = AIAgent.GetStageName(targetStage);
            string prefix = targetName + "的悄悄话回复：";
            if (reply.StartsWith(prefix))
                return reply.Substring(prefix.Length);
            return reply;
        }

        private void SaveOpeningCache(DeepSeekMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.content)) return;

            try
            {
                string hash = GetOcHash();
                string path = SLManager.ExportToJson(message, "Character/Cache", hash + ".json");
                if (!string.IsNullOrEmpty(path))
                    Debug.Log($"[开局缓存] {name} 成功开头已保存: {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[开局缓存] 保存失败: {e.Message}");
            }
        }

        private bool TryLoadOpeningCache(out DeepSeekMessage message)
        {
            message = null;
            try
            {
                string hash = GetOcHash();
                message = SLManager.ImportFromJson<DeepSeekMessage>("Character/Cache", hash + ".json");
                return message != null && !string.IsNullOrWhiteSpace(message.content);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[开局缓存] 读取失败: {e.Message}");
                return false;
            }
        }

        private string GetOcHash()
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(oc ?? "");
                byte[] hashBytes = md5.ComputeHash(bytes);
                var sb = new StringBuilder();
                foreach (byte b in hashBytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        [Serializable]
        private class WhisperCompressArgs
        {
            public int to;
            public string content;
        }

        private int N = 0;
        private int n_0 = 5;
        private float avgX = 1000f; // 实测平均每轮未压缩 token 数，种子值
        private float avgL = 200f;  // 实测平均每轮压缩后 token 数，种子值
        private const int k = 5;    // 保留最近完整轮数，与 last_round_index 的 5 对应
        private const float b = 30f; // DeepSeek 未命中/命中价格比

        // 连续系统录制回合未调用工具统计；超过 2 个系统回合未用工具时追加提醒
        private int roundsWithoutTool;
        private bool noToolReminderSent;

        // 思考模式回复校验与重试（从 AIRequest 移入；不合格时无限重试，直到合格）
        private const string ThinkingRetryPrompt =
            "[格式修正] 你上一条回复不合格。请重新输出：思考里必须用（我想：……）或(我想：……)；" +
            "真实回复禁止使用括号，禁止输出[skip]，必须有实际内容的公开发言，或者继续调用工具执行行动。";

        // ---------- AI 行为检查 ----------
        private const string BehaviorCheckerModel = "deepseek-v4-flash";

        private const string BehaviorCheckerSystemPrompt =
@"你是一个严格的游戏 AI 行为检查器。你的任务只有：判断目标 AI 是否“说了要做某个具体行动，但没有调用对应工具执行”。你不调用任何工具，只输出分析文字和最终判断。

目标 AI 可用的工具只有 3 个：
- use_prop：使用自己武器栏里的道具（护盾、霰弹、扫射、大球等）
- control_turret：控制自己炮塔瞄准
- whisper：给其他 AI 发悄悄话

判定规则：
1. 只有明确声称要做具体游戏行动、且对应工具没有出现在“实际工具调用”里时，才算缺失。
2. 情绪、嘲讽、喊口号、泛目标（如“我要赢”）不算缺失。
3. 发言只是在描述或配合已经发生的工具调用，不算缺失。
4. 分析过程中禁止使用方括号。最终判断必须单独放在最后一行。";

        private static readonly Regex BehaviorCheckVerdictRegex = new Regex(@"\[([^\[\]]*)\]", RegexOptions.Compiled);
        private static readonly string[] BehaviorCheckToolSeparators = { ",", "，", "、" };
        private static readonly HashSet<string> BehaviorCheckAllowedTools = new HashSet<string>
        {
            "use_prop",
            "control_turret",
            "whisper"
        };

        private bool openingRetryMode;
        private List<DeepSeekMessage> openingLastMessages;
        private int n_0_index = 1;//排除系统消息
        private List<int> last_round_index = new();

        private int EstimateHistoryTokens(List<DeepSeekMessage> messages)
        {
            int total = 0;
            foreach (DeepSeekMessage m in messages)
            {
                total += AIRequest.TokenEstimator.EstimateTokensCached(m.content);
                total += AIRequest.TokenEstimator.EstimateTokensCached(m.reasoning_content);
                total += AIRequest.TokenEstimator.EstimateTokensCached(m.name);

                if (m.tool_calls != null)
                {
                    foreach (ToolCall tc in m.tool_calls)
                    {
                        total += AIRequest.TokenEstimator.EstimateTokensCached(tc.function?.name);
                        total += AIRequest.TokenEstimator.EstimateTokensCached(tc.function?.arguments);
                        total += 4; // id 等开销
                    }
                }

                if (!string.IsNullOrEmpty(m.tool_call_id)) total += 4;
                total += 4; // role / 结构开销
            }
            return total;
        }

        private bool ShouldCompress()
        {
            // 前几轮先用种子值，等有实测数据再启用动态压缩
            if (N < 10 || avgX <= 0f || avgL <= 0f) return false;

            float L0 = (N - k) * avgL + k * avgX;
            if (L0 <= 0f) return false;

            float D = Mathf.Sqrt(2f * b * L0 / avgX);
            float d = N - n_0;
            return d >= D;
        }

        private bool MaybeCompress()
        {
            if (!ShouldCompress()) return false;

            Compress();

            // after  (N-k)*L + k*X，反解 L
            int after = EstimateHistoryTokens(history);
            int denominator = N - k;
            if (denominator > 0)
            {
                float newL = (after - k * avgX) / denominator;
                if (newL > 0f)
                    avgL = avgL * 0.7f + newL * 0.3f;
            }

            n_0 = N;
            return true;
        }

        public async Task UntilGreatRequest(int roundStartIndex)
        {
            TaskCompletionSource<bool> tcs = new();

            RequestInfo info = new(
                request,
                msgs =>
                {
                    if (!_isRunning) return;
                    // 检查 AI 回复 content，出现括号就在 history 末尾追加提醒，下一次请求会带过去
                    RemindIfUsesParentheses(msgs);
                    tcs.SetResult(true);
                    },
                error =>
                {
                    ReceiveError(error);
                    tcs.SetResult(true);
                },
                toolkit,
                true,
                position);

            info.apiKey = LoadApiKey();
            info.apiUrl = url;

            //保证已经检查完毕
            info.validateAndMaybeRetry = (msg) => {
                if (IsRoleplayReasoning(msg)) return true;
                info.AddMessage(new DeepSeekMessage("user", ThinkingRetryPrompt));
                Debug.LogWarning($"[AIRequest] 思考模式校验失败，已拦截工具并重发。reasoning: {msg.reasoning_content}");
                AIRequest.SendRequest(info);
                return false;
            };

            AIRequest.SendRequest(info);

            await tcs.Task;

            //Say
            if (SpeechPass > 0) return;

            // 只拼接本轮所有 content；本轮一条 content 都没有（空回复 / 只调了工具）就不发言，
            // 不往回找上一轮的对话，避免把旧发言重复显示一遍
            string roundContent = JoinRoundAssistantContents(roundStartIndex);
            if (!string.IsNullOrWhiteSpace(roundContent)) Say(position, roundContent);
        }
        private void Say(int position,string content)
        {
            InformGetter.SetAIContent(position, content);
            // 只剩他一个阵营之后，露的就是赢家的脸：强制盖掉模型自己写的 [emo:xxx]
            bool soloWinner = AIAgent.Instance != null && AIAgent.Instance.IsSoloWinner(position);
            if (Towel.AllTowel.TryGetValue(position, out Towel towel)) towel.Say(content, true);
            UIMessageManager.Instance?.AddMessage(new UIMInfo
            {
                stage = position,
                content = content,
                emo = soloWinner ? SpriteEmotion.win : SpriteEmotion.origin,   // 没写 [emo:xxx] 时的默认表情（Deal 里以标记为准）
                forceEmo = soloWinner
            });
        }
        public async Task FirstRequest(string inform)
        {
            N++;

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 5) last_round_index.RemoveAt(0);
            int startTokens = EstimateHistoryTokens(history);
            history.Add(new DeepSeekMessage("user", inform));

            int roundStartIndex = history.Count;

            // 有成功缓存就直接复用，不再请求/重刷
            if (TryLoadOpeningCache(out DeepSeekMessage cachedOpening))
            {
                history.Add(cachedOpening);
                Say(position, cachedOpening.content);

                return;
            }
            request.tool_choice = "none";
            //请求直到正确
            await UntilGreatRequest(roundStartIndex);

            SaveOpeningCache(request.messages[^1]);

            request.tool_choice = "auto"; // 开局结束后恢复自动工具调用

            int endTokens = EstimateHistoryTokens(history);
            int sampleX = Mathf.Max(0, endTokens - startTokens);
            avgX = avgX * 0.9f + sampleX * 0.1f;

            Save();
        }

        public async Task NormalRequest(string inform)
        {
            N++;
            bool compressed = MaybeCompress();

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 5) last_round_index.RemoveAt(0);

            int startTokens = EstimateHistoryTokens(history);
            history.Add(new DeepSeekMessage("user", inform));

            // 压缩后不能只改旧历史；要在“这轮新对话”里追加 Tool 使用方法提醒
            if (compressed)
                history.Add(new DeepSeekMessage("user", BuildToolUsageReminder()));

            int roundStartIndex = history.Count;

            //请求直到正确
            await UntilGreatRequest(roundStartIndex);

            int endTokens = EstimateHistoryTokens(history);
            int sampleX = Mathf.Max(0, endTokens - startTokens);
            avgX = avgX * 0.9f + sampleX * 0.1f;

            //言行一致性检查
            List<string> missingTools = await RunBehaviorCheckAsync(roundStartIndex);
            foreach (string toolName in missingTools) await ForceToolCallAsync(toolName);
            //连续未用工具”统计
            UpdateToolUsageReminder(roundStartIndex);

            Save();
        }


        /// <summary>拼接本轮所有 assistant.content；本轮没有任何 content 时返回空串。</summary>
        private string JoinRoundAssistantContents(int roundStartIndex)
        {
            var parts = new List<string>();
            for (int i = roundStartIndex; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg != null && msg.role == "assistant" && !string.IsNullOrWhiteSpace(msg.content))
                    parts.Add(msg.content.Trim());
            }
            return string.Join("\n", parts);
        }

        /// <summary>拼接本轮所有 assistant.content，空时以“（无）”占位（给检查模型看）。</summary>
        private string CollectRoundAssistantContents(int roundStartIndex)
        {
            string joined = JoinRoundAssistantContents(roundStartIndex);
            return string.IsNullOrEmpty(joined) ? "（无）" : joined;
        }

        /// <summary>收集本轮所有工具调用（工具名 + 参数）。</summary>
        private string CollectRoundToolCalls(int roundStartIndex)
        {
            var parts = new List<string>();
            for (int i = roundStartIndex; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg == null || msg.role != "assistant" || msg.tool_calls == null) continue;

                foreach (ToolCall call in msg.tool_calls)
                {
                    string callName = call?.function?.name;
                    if (string.IsNullOrWhiteSpace(callName)) continue;
                    string arguments = call?.function?.arguments;
                    parts.Add(string.IsNullOrWhiteSpace(arguments) ? callName : $"{callName} {arguments}");
                }
            }
            return parts.Count > 0 ? string.Join("\n", parts) : "无";
        }

        private string BuildBehaviorCheckUserPrompt(string roundContents, string roundToolCalls)
        {
            return $@"请检查以下 AI 本轮行为：

【本轮公开发言（按顺序拼接）】
{roundContents}

【本轮实际工具调用】
{roundToolCalls}

输出要求：
1. 先输出你的分析。
2. 最后一行只输出最终判断，格式：
   - 没有缺失：[]
   - 有缺失：[工具名]
   - 缺多个：用英文逗号分隔，例如 [use_prop,control_turret]
3. [] 里只能出现 use_prop、control_turret、whisper，不得输出其他内容。";
        }

        /// <summary>从检查模型的输出里正则提取最后的 [工具名] 判断。</summary>
        private List<string> ParseBehaviorCheckVerdict(string checkerText)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(checkerText)) return result;

            MatchCollection matches = BehaviorCheckVerdictRegex.Matches(checkerText);
            if (matches.Count == 0) return result;

            string verdict = matches[matches.Count - 1].Groups[1].Value;
            if (string.IsNullOrWhiteSpace(verdict)) return result;

            foreach (string raw in verdict.Split(BehaviorCheckToolSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string toolName = raw.Trim();
                if (string.IsNullOrEmpty(toolName)) continue;

                if (BehaviorCheckAllowedTools.Contains(toolName))
                {
                    if (!result.Contains(toolName))
                        result.Add(toolName);
                }
                else
                {
                    Debug.LogWarning($"[AI行为检查] {name} 模型判断里出现未知工具：{toolName}。原文：{checkerText}");
                }
            }
            return result;
        }

        private static readonly List<string> 游戏关键词 = new() { "大球", "护盾", "弹药", "霰弹" };
        /// <summary>
        /// 行为检查：把本轮 content 与工具调用交给 thinking disabled 的 flash 模型判断。
        /// 返回“说了要做但没调用”的工具名列表。
        /// </summary>
        private async Task<List<string>> RunBehaviorCheckAsync(int roundStartIndex)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string roundContents = CollectRoundAssistantContents(roundStartIndex);

            if (游戏关键词.TrueForAll(s => !roundContents.Contains(s))) return new();

            string roundToolCalls = CollectRoundToolCalls(roundStartIndex);

            DeepSeekRequest checkerRequest = new DeepSeekRequest
            {
                model = BehaviorCheckerModel,
                temperature = 0f,
                max_tokens = 1024,
                stream = false,
                thinking = new ThinkingConfig(false)
            };
            checkerRequest.messages = new List<DeepSeekMessage>
            {
                new DeepSeekMessage("system", BehaviorCheckerSystemPrompt),
                new DeepSeekMessage("user", BuildBehaviorCheckUserPrompt(roundContents, roundToolCalls))
            };
            checkerRequest.tools = null;
            checkerRequest.tool_choice = null;
            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new RequestInfo(
                checkerRequest,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m != null && m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? last.content : "");
                },
                error =>
                {
                    Debug.LogWarning($"[AI行为检查] {name} 检查请求失败：{error}");
                    tcs.TrySetResult("");
                },
                toolkit: null,
                back_tool: false,
                toolStage: position);
            info.apiKey = AIAgent.LoadApiKey();
            info.apiUrl = url;

            try
            {
                AIRequest.SendRequest(info);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI行为检查] {name} 发送检查请求异常：{e.Message}");
                tcs.TrySetResult("");
            }

            string checkerText = await tcs.Task;

            sw.Stop();

            List<string> missingTools = ParseBehaviorCheckVerdict(checkerText);
            Debug.Log($"[AI行为检查] {name} 检查完成，用时 {sw.Elapsed.TotalSeconds:F2}s。\n" +
                      $"发言：{roundContents}\n工具调用：{roundToolCalls}\n模型输出：\n{checkerText}\n" +
                      $"缺失工具：{(missingTools.Count > 0 ? string.Join(", ", missingTools) : "无")}");
            if (missingTools.Count > 0)
                Debug.LogWarning($"[AI行为检查] {name} 检测到说做未做：{string.Join(", ", missingTools)}");

            return missingTools;
        }

        /// <summary>
        /// 用 tool_choice 强制 AI 调用指定工具一次（back_tool=false，不生成后续公开发言）。
        /// 多个缺失工具由调用方逐个分批发。
        /// </summary>
        private async Task<bool> ForceToolCallAsync(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName) || !BehaviorCheckAllowedTools.Contains(toolName))
                return false;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int forceStartIndex = history.Count;
            history.Add(new DeepSeekMessage("user",
                $"[行为检查补执行] 检测到你上一轮声称要做某个行动，但没有调用 {toolName}。现在必须调用 {toolName} 实际执行，具体参数根据你收到的情报自行决定，不要只说话。"));

            object previousToolChoice = request.tool_choice;
            request.tool_choice = new { type = "function", function = new { name = toolName } };
            var tcs = new TaskCompletionSource<bool>();
            RequestInfo info = new RequestInfo(
                request,
                msgs =>
                {
                    if (msgs != null)
                    {
                        foreach (DeepSeekMessage msg in msgs)
                        {
                            // back_tool=false 时这里收到的是 role=tool 结果，落回 history
                            if (msg != null && msg.role == "tool")
                                history.Add(msg);
                        }
                    }
                    tcs.TrySetResult(true);
                },
                error =>
                {
                    Debug.LogWarning($"[AI行为检查] {name} 强制调用 {toolName} 失败：{error}");
                    tcs.TrySetResult(true);
                },
                toolkit,
                back_tool: false,
                toolStage: position);
            info.apiKey = AIAgent.LoadApiKey();
            info.apiUrl = url;

            try
            {
                AIRequest.SendRequest(info);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AI行为检查] {name} 强制调用 {toolName} 异常：{e.Message}");
                tcs.TrySetResult(true);
            }

            try
            {
                await tcs.Task;
            }
            finally
            {
                request.tool_choice = previousToolChoice;
            }

            sw.Stop();

            bool executed = false;
            for (int i = forceStartIndex + 1; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg != null && msg.role == "tool" && !string.IsNullOrEmpty(msg.tool_call_id))
                {
                    executed = true;
                    break;
                }
            }

                        Debug.Log($"[AI行为检查] {name} 强制调用 {toolName} 完成，用时 {sw.Elapsed.TotalSeconds:F2}s，实际执行：{(executed ? "是" : "否")}");

            return executed;
        }

        private string BuildToolUsageReminder()
        {
            var sb = new StringBuilder("[压缩后的Tool提醒] 旧历史已被压缩，但你仍然可以正常调用工具，调用方式不变。可用工具：");

            bool first = true;
            if (request.tools != null)
            {
                foreach (Tool tool in request.tools)
                {
                    string toolName = tool?.function?.name;
                    if (string.IsNullOrWhiteSpace(toolName)) continue;
                    if (!first) sb.Append("、");
                    sb.Append(toolName);
                    first = false;
                }
            }

            if (first)
                sb.Append("（无）");
            else
                sb.Append("。请按工具参数 JSON 调用，不要因为历史被压缩就只发文字。");

            return sb.ToString();
        }

        private void UpdateToolUsageReminder(int roundStartIndex)
        {
            bool usedToolThisRound = false;
            for (int i = roundStartIndex; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg == null) continue;
                if (msg.role == "tool" || (msg.tool_calls != null && msg.tool_calls.Count > 0))
                {
                    usedToolThisRound = true;
                    break;
                }
            }

            if (usedToolThisRound)
            {
                roundsWithoutTool = 0;
                noToolReminderSent = false;
                return;
            }

            roundsWithoutTool++;

            // 超过 2 个系统录制回合没用工具：在第 3 个连续空回合追加一次提醒
            if (roundsWithoutTool >= 3 && !noToolReminderSent)
            {
                noToolReminderSent = true;
                history.Add(new DeepSeekMessage("user",
                    $"[行动提醒] 你已经连续 {roundsWithoutTool} 个系统录制回合没有调用工具。请尽快使用工具采取实际游戏行动，不要只发言或思考。注意：道具不是弹珠，不会越养越大！数值小且没用的道具应该尽快用掉。"));
                Debug.LogWarning($"[AIAgent] {name} 连续 {roundsWithoutTool} 个系统回合未调用工具，已追加行动提醒");
            }
        }
        private const string ParenthesisReminder =
            "[格式提醒] 检测到你上次的回复里带了中文或英文括号。下次回复禁止使用任何括号，请严格遵守：\n" +
            "1. 纯文本 + emoji，禁用 markdown，不使用括号，不要动作描写。\n" +
            "2. 别露内心戏，别露你的情报。\n" +
            "3. 夸张化地沉浸在角色中，字数限制在 25 字。";

        private static readonly char[] ParenthesisChars = { '（', '）', '(', ')' };

        private bool ContainsParenthesis(string text)
        {
            return !string.IsNullOrEmpty(text) && text.IndexOfAny(ParenthesisChars) >= 0;
        }

        private void RemindIfUsesParentheses(List<DeepSeekMessage> messages)
        {
            if (messages == null) return;

            foreach (DeepSeekMessage message in messages)
            {
                if (message == null || message.role != "assistant") continue;
                if (!ContainsParenthesis(message.content)) continue;

                // 避免同一个违规反复堆叠提醒
                if (history.Count > 0)
                {
                    DeepSeekMessage last = history[history.Count - 1];
                    if (last.role == "user" && last.content == ParenthesisReminder)
                        return;
                }

                history.Add(new DeepSeekMessage("user", ParenthesisReminder));
                Debug.Log($"[AIAgent] {name} 的回复包含括号，已追加下次禁止括号提醒。原文: {message.content}");
                return;
            }
        }


        private void DisplayReceivedMessages(List<DeepSeekMessage> messages)
        {
            foreach (DeepSeekMessage message in messages)
            {
                if (message.role == "assistant")
                {
                    string content = message.content;
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    if (content.Contains("[skip]")) continue;

                    InformGetter.SetAIContent(position, content);
                    if (Towel.AllTowel.TryGetValue(position, out Towel towel))
                    {
                        towel.Say(content);
                    }
                }
            }
        }
        private void ReceiveError(string error)
        {
            Debug.LogError("AI请求错误:"+error);
        }
        private DeepSeekMessage GetLastAssistantMessage()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].role == "assistant")
                    return history[i];
            }
            return null;
        }

        private string GetLastAssistantReasoning(out string content)
        {
            content = "";
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].role == "assistant")
                {
                    content = history[i].content;
                    return history[i].reasoning_content;
                }
            }
            return null;
        }
        #region SL
        public void Save()
        {
            SLManager.ExportToJson(this, "Characters", name);
        }
        public static bool Load(string name,out CharacterCard card)
        {
            card = SLManager.ImportFromJson<CharacterCard>("Characters", name);
            if (card.request.messages != null)
            {
                card.history = card.request.messages;
            }
            if (card == null) return false;
            return true;
        }
        #endregion
    }
    #endregion
}
