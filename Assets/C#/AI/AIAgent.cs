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
/// 升级选择的完整结果：选择项 + AI 的思考内容（升级选择演出要用）。
/// </summary>
public class UpgradeChoiceResult
{
    /// <summary>1 = 额外弹珠，2 = 炮塔强化，3 = 护盾强化。</summary>
    public int choice = 1;
    /// <summary>AI 在“思考”那一步写的 reasoning_content（拿不到就空串）。</summary>
    public string thinking = "";
    /// <summary>AI 自己的正文回复（充当它“说出口的话”，可能为空）。</summary>
    public string speech = "";
}

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

    /// <summary>一轮的间隔秒数（面板上的 _cycleInterval）。情报的滞后量，以及"剩余时间不够一轮就别报"的门槛都用它。</summary>
    public static float RoundInterval => Instance != null ? Instance._cycleInterval : 0f;

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

    // ==================== 三段格式：内容写在【分析：…】【我要：…】【我说：…】里面 ====================
    // 思考（reasoning）已经全部关掉，模型的"想"写在 content 的第一段里。三段各有去处：
    //   分析 → 舞台的思考条（也是它的内心独白，带（我想：…））
    //   我要 → 这一轮打算做什么（给行为检查用，不当台词）
    //   我说 → 唯一会被当众播出去的那一句（飘字、发言列表、给对手读的【上一轮发言】）

    /// <summary>取某一段的内容（label 传 "分析" / "我要" / "我说"）；没有这一段返回 null。
    /// 全角/半角冒号都认（模型偶尔写成 ASCII 冒号）。</summary>
    public static string Segment(string content, string label)
    {
        if (string.IsNullOrEmpty(content)) return null;
        int i = content.IndexOf("【" + label + "：", StringComparison.Ordinal);
        if (i < 0) i = content.IndexOf("【" + label + ":", StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + label.Length + 2;                 // 跳过 【 与 ：
        int end = content.IndexOf('】', start);
        if (end < 0) end = content.Length;
        return content.Substring(start, end - start).Trim();
    }

    /// <summary>
    /// 取【我说】：公开台词。**没有这个标记时退回整段 content**（
    /// 遗言、悄悄话这类不走三段格式的请求不至于变成空话）；有三段的本轮回复由校验保证标记齐全。
    /// </summary>
    public static string SayOf(string content)
    {
        string say = Segment(content, "我说");
        if (say != null) return say;
        return (content ?? "").Trim();
    }

    /// <summary>取【分析】：内心独白。没有标记就退回整段（舞台思考条照旧能显示）。</summary>
    public static string AnalysisOf(string content)
    {
        string a = Segment(content, "分析");
        if (a != null) return a;
        return (content ?? "").Trim();
    }

    /// <summary>取【我要】：这一轮打算做什么。</summary>
    public static string WantOf(string content) => Segment(content, "我要") ?? "";

    /// <summary>【我说】那段去掉 [emo:xxx] 之后的字数（15 字限制按这个算）。</summary>
    public static int SayLength(string content)
    {
        string say = SayOf(content);
        say = System.Text.RegularExpressions.Regex.Replace(say, @"\[emo:[a-zA-Z]+\]", "");
        return say.Trim().Length;
    }

    /// <summary>这一段写全了吗：有开头「【xx：」也要有收尾的「】」。
    /// （Segment 为了兜底会把"没有收尾】"当成取到结尾，所以校验要单独卡这一条。）</summary>
    public static bool HasClosedSegment(string content, string label)
    {
        if (string.IsNullOrEmpty(content)) return false;
        int i = content.IndexOf("【" + label + "：", StringComparison.Ordinal);
        if (i < 0) i = content.IndexOf("【" + label + ":", StringComparison.Ordinal);
        if (i < 0) return false;
        return content.IndexOf('】', i + label.Length + 2) >= 0;
    }

    /// <summary>
    /// 三段格式校验（本轮回复）：
    /// 必须有【分析：…】和【我说：…】；三段都要写全（带收尾的 】）；
    /// 【分析】里要有（我想：…）；【我说】不许有括号。
    /// allowEmptySay = 允许【我说】为空（开局那句长期规划就是只想不说）。
    /// </summary>
    public static bool IsThreeSegmentReply(DeepSeekMessage message, bool allowEmptySay = false)
    {
        if (message == null) return false;
        string content = message.content ?? "";

        // 三段都要写全：缺开头、缺收尾的 】都算不合格
        foreach (string label in SegLabels)
            if (!HasClosedSegment(content, label)) return false;

        string analysis = Segment(content, "分析");
        if (string.IsNullOrWhiteSpace(analysis)) return false;
        if (!analysis.Contains("（我想：")) return false;

        string say = Segment(content, "我说");
        bool hasTools = message.tool_calls != null && message.tool_calls.Count > 0;
        // 【我说】为空：调用了工具（它在行动，不说也行）或这一轮本来就是只想不说（allowEmptySay）才放行
        if (!allowEmptySay && !hasTools && string.IsNullOrWhiteSpace(say)) return false;
        if (ContainsAnyParenthesis(say)) return false;
        if (say.Contains("我想：") || say.Contains("我想:")) return false;

        return true;
    }

    private static readonly string[] SegLabels = { "分析", "我要", "我说" };

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

    [Header("移动视野截图（AI 想移动时，附一张炮塔周围的圆形俯视图）")]
    [Tooltip("关掉就不截图：AI 移动前看不到周围，也不占请求 token")]
    public bool moveSightEnabled = true;
    // 截图半径倍率搬到了 MapConfig（moveSightRadiusFactor）：放在场景组件上会被 Unity 重新序列化冲掉，
    // 而且它本质是玩法口径，跟移动距离是一套东西，统一在 MapConfig 上配。
    [Tooltip("输出图片边长（像素）。太小看不清炮塔，太大费 token")]
    public int moveSightResolution = 512;
    private readonly HashSet<int> deadStages = new();

    /// <summary>开局那一轮正在进行：这一轮的发言交给开场演出按顺序播，不走飘字 / 立绘列表。</summary>
    private bool openingRoundActive;
    public bool OpeningRoundActive => openingRoundActive;
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
        // 录制暂停的兜底看门狗：真正卡住时至少不会一直冻着（详见 CapturePause.Tick）
        CapturePause.Tick();
        // 预行动：每帧轮询条件，满足就替 AI 执行（没有预行动时它自己会秒退）
        ReadyActionManager.Tick();
        StartCycle();
    }

    /// <summary>当前全局回合数（情报里要显示；预行动也用它做轮次口径）。</summary>
    public static int CurrentRound { get; private set; }

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
            CurrentRound = _round;
            if (_round == 1) ReadyActionManager.ResetAll();   // 新一局：预行动与缓存全部清空


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
        // 这整个方法必须保证 complete 一定被调用：RunCycleLoop 是
        // `CapturePause.Pause(); await tcs.Task; CapturePause.Resume();`，
        // 而 tcs 只由 complete 触发。这里如果抛出异常（例如 RunBehaviorCheckAsync /
        // ForceToolCallAsync 出错），complete 就永远不会被调用 —— 录制会一直停在暂停态、
        // _isWaiting 也一直是 true，后面每一轮都在 "Previous round still waiting" 里空转。
        // 所以整段包在 try/catch/finally 里，无论怎么退出都把 complete 交出去。
        try
        {
            await RunAIAnalysicCore(complete);
        }
        catch (Exception e)
        {
            Debug.LogError($"[AIAgent] 本轮 AI 流程异常，已结束本轮并恢复录制：{e}");
        }
        finally
        {
            complete?.Invoke();
        }
    }

    private async Task RunAIAnalysicCore(Action complete)
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

        // 标签外的每轮补充消息（上一轮发言 + 存活状态 + 战况）：全场共用一条，不进情报标签所以不会被压缩
        string roundExtra = InformGetter.BuildRoundExtra();

        // Debug 耗时：这一轮 4 张卡是并行跑的 —— 秒表**必须在建任务之前**起（RunCardAsync 一调用就会
        // 同步跑到第一个 await，从那一刻各家就开始计时了），否则全轮会比最慢那家还短，看着对不上。
        System.Diagnostics.Stopwatch roundWallWatch = System.Diagnostics.Stopwatch.StartNew();
        List<Task> tasks = new List<Task>();
        bool openingRound = _round == 0;
        openingRoundActive = openingRound;
        for (int i = 0; i < cards.Count; i++)
        {
            CharacterCard card = cards[i];
            if (deadStages.Contains(card.position)) continue;
            StringBuilder builder;
            if (cycle_start)
            {
                builder = new(CharacterCard.ModePrompt);
                builder.AppendLine();
                // 第一句只说赛前狠话（长期规划是第二句、另一个请求，提示词见 CharacterCard.OpeningPlanPrompt）。
                // 赛前这一句特意：① 点名要凶要狂、可以点名道姓；② 让它拿「你的上一局」那段回顾当火药（记仇、算旧账）；
                // ③ 字数例外放宽到 25 字（其他回合仍是 15 字）。
                builder.AppendLine("另外游戏开始，请各位选手在赛前放狠话。");
                builder.AppendLine("这是赛前环节，**要凶、要狂、要挑衅，可以点名道姓**：谁上一局打过你、抢过你的地、坑过你、最后赢了，直接点他的名字说事。");
                builder.AppendLine("**必须依据系统提示里「你的上一局」那段回顾来放**：翻旧账、揭老底、把上局的恩怨变成这一局的火药；别客气、别端着，也别写「我不急，你们先动」这种温吞话。");
                builder.AppendLine("这一句例外放宽：**【我说】可以写到 25 字以内**（其他回合仍限 15 字），纯文本 + emoji、不许括号、不要动作描写。");
            }
            else
            {
                builder = new();
                InformGetter.GetInfo(builder, card.position);
            }

            tasks.Add(RunCardAsync(card, builder.ToString(), roundExtra));
        }
        InformGetter.ClearDamageStats();
        InformGetter.ClearImpactStats();
        if (_round == 0)
        {
            EnterOpeningRetryPause();
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks);
        roundWallWatch.Stop();
        CharacterCard.LogMergedRoundTiming(_round, roundWallWatch.Elapsed.TotalMilliseconds);
        openingRoundActive = false;

        // 一轮缓冲：本轮结束后清空发言缓冲、写入本轮发言，供下一轮情报读取
        InformGetter.CommitRoundSpeeches();

        if (openingRound)
        {
            // 用 try/finally 保证计数一定退回去：PlayOpeningScene 里任何异常都会让
            // ExitOpeningRetryPause 被跳过，capturePauseCount 停在 1，后面所有 Enter 都会变成
            // 「计数 +1 但不再 Pause」，而 Exit 永远退不到 0 —— 录制就再也不会恢复。
            try
            {
                ExitOpeningRetryPause();
                PlayOpeningScene();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AIAgent] 开场演出异常：{e}");
            }
            finally
            {
                // 计数归零兜底：万一上面的流程漏掉一次 Exit，也不会让之后的暂停整体偏移
                capturePauseCount = 0;
            }
        }
    }

    public bool skipScene = false;

    /// <summary>
    /// 开场一条龙，三段在同一个舞台上连着演（背景不重开，所以是无缝的）：
    ///   1) 游戏规则介绍  2) 四位角色的赛前狠话（人设 + 狠话同时弹出，每位 3 秒）  3) 「游戏正式开始」横幅。
    /// 原来的做法是四座炮塔同时飘字，互相盖住，现在改成一个人一个人上台。
    /// </summary>
    private void PlayOpeningScene()
    {
        if (!StoryTeller.CanPlay) return;

        var lines = new List<OpeningTalkScene.Line>();
        foreach (CharacterCard card in cards)
        {
            if (deadStages.Contains(card.position)) continue;
            string speech = string.IsNullOrWhiteSpace(card.openingSpeech)
                ? "……"
                : ExtractEmotion(card.openingSpeech, out _, out _).Trim();
            lines.Add(new OpeningTalkScene.Line
            {
                owner = card.position,
                persona = card.oc ?? "",
                speech = speech
            });
        }
        if (lines.Count == 0) return;

        if (!skipScene)
        {
            StoryTeller show = StoryTeller.Instance;
            show.Play(new OpeningRulesScene());                 // 开场介绍（7/9/5/13/5/9 秒分镜）
            show.Play(new OpeningTalkScene(lines));             // 角色开场白
            show.Play(new GameStartBannerScene());              // 游戏正式开始
            Debug.Log($"[开场演出] 已入队：开场介绍 + {lines.Count} 位角色 + 开始横幅。");
        }
    }

    private async Task RunCardAsync(CharacterCard card, string inform, string extra)
    {
        WhisperManager.SetBusy(card.position, true);
        try
        {
            if (deadStages.Contains(card.position)) return;
            if(_round == 0) await card.FirstRequest(inform, extra);
            else await card.NormalRequest(inform, extra);
        }
        finally
        {
            WhisperManager.SetBusy(card.position, false);
        }
    }

    private const string ChooseUpgradeToolName = "choose_upgrade";

    /// <summary>升级三选一的临时工具：只在这次强制选择里挂上，不进卡片常驻工具表。</summary>
    private static Tool BuildChooseUpgradeTool()
    {
        return new Tool
        {
            type = "function",
            function = new Function
            {
                name = ChooseUpgradeToolName,
                description = "提交本次空槽升级的选择。必须调用且只能调用一次：choice=1 额外弹珠，2 炮塔强化，3 护盾强化。",
                parameters = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        {
                            "choice",
                            new
                            {
                                type = "integer",
                                @enum = new List<int> { 1, 2, 3 },
                                description = "1=额外弹珠（立即生成并发射两枚新弹珠）；2=炮塔强化；3=护盾强化。"
                            }
                        }
                    },
                    required = new List<string> { "choice" }
                }
            }
        };
    }

    /// <summary>升级选择专用工具处理器：只把选择回执成一条 tool 消息，不改任何游戏状态。</summary>
    private class UpgradeChoiceToolkit : Itool
    {
        public Task<List<DeepSeekMessage>> DealToolCallsAsync(List<ToolCall> toolCalls, int stage = -1)
        {
            var result = new List<DeepSeekMessage>();
            if (toolCalls == null) return Task.FromResult(result);

            foreach (ToolCall call in toolCalls)
            {
                int choice = ParseUpgradeChoiceFromToolCall(call);
                result.Add(new DeepSeekMessage
                {
                    role = "tool",
                    content = $"已记录本次升级选择：{choice}（{UpgradeChoiceName(choice)}）。",
                    tool_call_id = call != null ? call.id : ""
                });
            }
            return Task.FromResult(result);
        }
    }

    private readonly UpgradeChoiceToolkit upgradeChoiceToolkit = new();

    [Serializable]
    private class UpgradeChoiceArgs
    {
        public int choice;
        public string choice_name = "";
    }

    private static string UpgradeChoiceName(int choice)
        => choice == 3 ? "护盾强化" : (choice == 2 ? "炮塔强化" : "额外弹珠");

    /// <summary>从 choose_upgrade 工具调用的参数里取选择；拿不到就默认 1（额外弹珠）。</summary>
    private static int ParseUpgradeChoiceFromToolCall(ToolCall call)
    {
        string args = call?.function?.arguments;
        if (string.IsNullOrWhiteSpace(args)) return 1;

        upgradeChoiceFallbackText = "";
        try
        {
            UpgradeChoiceArgs parsed = JsonConvert.DeserializeObject<UpgradeChoiceArgs>(args);
            if (parsed != null)
            {
                if (parsed.choice >= 1 && parsed.choice <= 3) return parsed.choice;
                upgradeChoiceFallbackText = parsed.choice_name ?? "";
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[升级选择] 参数解析失败：{args}（{e.Message}）");
            upgradeChoiceFallbackText = args;
        }

        // 兑底：模型把选项名或数字写进了参数里（不是标准 choice）
        string text = upgradeChoiceFallbackText ?? "";
        if (text.Contains("护盾")) return 3;
        if (text.Contains("炮塔")) return 2;
        if (text.Contains("弹珠")) return 1;
        foreach (char c in text)
            if (c >= '1' && c <= '3') return c - '0';
        return 1;
    }

    private static string upgradeChoiceFallbackText = "";

    /// <summary>文字兑底解析（模型没按工具调用回答时）：关键词 → 数字 → 默认 1。</summary>
    private static int ParseUpgradeChoiceFromText(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 1;

        if (content.Contains("护盾")) return 3;
        if (content.Contains("炮塔") || content.Contains("后坐力") || content.Contains("转速")) return 2;
        if (content.Contains("弹珠")) return 1;

        foreach (char c in content)
            if (c >= '1' && c <= '3') return c - '0';
        return 1;
    }

    private const string UpgradeCommitPrompt =
        "现在提交你的选择：调用 choose_upgrade 工具，choice 只能填 1（额外弹珠）、2（炮塔强化）或 3（护盾强化）。" +
        "必须给出与上面分析一致的选择，不要用文字回答。";

    /// <summary>升级选择「思考」步的格式修正提示（与普通行动轮同一套口径：不合格就一直重试）。</summary>
    private const string UpgradeThinkingRetryPrompt =
        "[格式修正] 你上一条回复不合格。请重新输出三段格式：【分析：……】里用（我想：……）写内心独白；" +
        "【我要：……】写这一轮打算做什么；【我说：……】写当众那句（没有就不写内容）；" +
        "然后必须调用 choose_upgrade 工具给出 1 / 2 / 3 的选择，不要用文字解释。";

    /// <summary>
    /// 升级选择「思考」步的回复校验：必须有【分析：…】且里面带（我想：……），【我说】不许带括号。
    /// 这里**不豁免工具调用**——升级演出要把这段分析播给观众看，所以哪怕模型第一步就调用了
    /// choose_upgrade，也要求它带上这三段。
    /// </summary>
    private static bool IsUpgradeThinkingValid(DeepSeekMessage msg)
    {
        if (msg == null) return false;
        string analysis = Segment(msg.content, "分析");
        if (string.IsNullOrWhiteSpace(analysis) || !analysis.Contains("（我想：")) return false;
        string say = Segment(msg.content, "我说");
        if (say != null && ContainsAnyParenthesis(say)) return false;
        return true;
    }

    /// <summary>发一次升级相关的请求，返回本次新增的 assistant 回复与 tool 回执（失败时 assistant 为 null）。
    /// requireThinkingMark = true 时给这次请求挂上「思考格式校验」：不合格就无限重发，直到合格为止。</summary>
    private async Task<(DeepSeekMessage assistant, List<DeepSeekMessage> toolReplies)> SendUpgradeRequest(
        DeepSeekRequest request, CharacterCard card, int stage, string label, bool requireThinkingMark = false)
    {
        int before = request.messages != null ? request.messages.Count : 0;

        var tcs = new TaskCompletionSource<bool>();
        List<DeepSeekMessage> toolReplies = null;
        RequestInfo info = new RequestInfo(request,
            msgs => { toolReplies = msgs; tcs.TrySetResult(true); },
            error => { Debug.LogWarning($"[升级选择] stage {stage} {label}请求失败：{error}"); tcs.TrySetResult(false); },
            toolkit: upgradeChoiceToolkit,
            back_tool: false,
            toolStage: stage);
        info.apiKey = LoadApiKey();
        info.apiUrl = card.url;

        // 思考步的格式校验：和普通行动轮同一套做法 —— 校验器返回 false 时 AIRequest 会中断本次响应，
        // 由这里重新 SendRequest。**不设重试上限**（用户要求无限重试），所以每次都要打日志，方便在 Console 里看卡在哪。
        if (requireThinkingMark)
        {
            DeepSeekMessage retryReminder = null;
            int retryCount = 0;
            info.validateAndMaybeRetry = msg =>
            {
                if (IsUpgradeThinkingValid(msg))
                {
                    // 合格：把这次插进去的 [格式修正] 摘掉，别把提醒留在上下文里
                    if (retryReminder != null)
                    {
                        request.messages.Remove(retryReminder);
                        retryReminder = null;
                    }
                    return true;
                }

                retryCount++;
                if (retryReminder == null)
                {
                    retryReminder = new DeepSeekMessage("user", UpgradeThinkingRetryPrompt);
                    info.AddMessage(retryReminder);   // 和普通轮一样：同一步只插一条提醒，不重复堆
                }

                Debug.LogWarning($"[升级选择] stage {stage} {label}步校验不合格（第 {retryCount} 次重试）：" +
                                 $"三段齐全={IsThreeSegmentReply(msg, true)}，content: {msg?.content}");

                AIRequest.SendRequest(info);   // 无限重试
                return false;
            };
        }

        AIRequest.SendRequest(info);
        await tcs.Task;

        DeepSeekMessage assistant = null;
        if (request.messages != null)
        {
            for (int i = request.messages.Count - 1; i >= before; i--)
            {
                DeepSeekMessage m = request.messages[i];
                if (m != null && m.role == "assistant") { assistant = m; break; }
            }
        }
        return (assistant, toolReplies);
    }

    /// <summary>从回复里取出 choose_upgrade 的工具调用（没有就取任意工具调用）。</summary>
    private static ToolCall FindChooseUpgradeCall(DeepSeekMessage message)
    {
        if (message?.tool_calls == null || message.tool_calls.Count == 0) return null;
        return message.tool_calls.FirstOrDefault(c => c?.function?.name == ChooseUpgradeToolName)
            ?? message.tool_calls[0];
    }

    /// <summary>
    /// 把用完的移动截图降级成纯文本。stage &lt; 0 时四张卡全清，否则只清这一张。
    /// 给 ReactionSystem 用：同一轮里连续预览好几次时，只保留最新那张截图。
    /// </summary>
    public static void DropMoveShotImages(int stage = -1)
    {
        AIAgent agent = Instance;
        if (agent == null) return;

        if (stage < 0)
        {
            foreach (CharacterCard c in agent.cards) c?.DropMoveShotImages();
            return;
        }

        CharacterCard card = agent.cards.Find(c => c.position == stage);
        card?.DropMoveShotImages();
    }

    /// <summary>旧签名：只要选择项（1 额外弹珠 / 2 炮塔强化 / 3 护盾强化）。</summary>
    public async Task<int> RequestUpgradeChoiceAsync(int stage)
    {
        UpgradeChoiceResult result = await UpgradeChoiceRequestAsync(stage);
        return result != null ? result.choice : 1;
    }

    /// <summary>
    /// 升级触发时调用，两步走（都不占行动轮）：
    /// 1) 思考：thinking 开、tool_choice = null（工具表给上）——先让 AI 分析该选哪个，它也可能直接调用工具；
    /// 2) 提交：thinking 关、tool_choice 强制 choose_upgrade——强制 tool_choice 与思考模式互斥（实测开思考会 400）。
    /// 两步的完整新增对话（user 提示、assistant 思考、user 提交指令、assistant 工具调用、tool 回执）都写回 card.history。
    /// 返回值带着 AI 的思考，供升级选择演出先播思考、再揭晓结果。
    /// </summary>
    public async Task<UpgradeChoiceResult> UpgradeChoiceRequestAsync(int stage)
    {
        UpgradeChoiceResult result = new UpgradeChoiceResult();
        // 已出局就不再询问：死亡时道具栈被清空，空槽反而最多、升级攒得最快，不拦的话会对着死者再演一场
        if (deadStages.Contains(stage)) return result;
        CharacterCard card = cards.Find(c => c.position == stage);
        if (card == null) return result;

        // 升级请求是从 card.request 深拷贝出来的：先把用过的截图降级掉，免得把旧图一起拷进去
        card.DropMoveShotImages();

        int choice = 1;
        CapturePause.Pause();
        try
        {
            var exchange = new List<DeepSeekMessage>();
            var promptMessage = new DeepSeekMessage("user", BuildUpgradeChoicePrompt(card));
            exchange.Add(promptMessage);

            // ---------- 第一步：分析（thinking 已全面关掉，独白写在【分析】里；tool_choice = null） ----------
            DeepSeekRequest thinkRequest = card.request.DeepCopy();
            if (thinkRequest.messages == null) thinkRequest.messages = new List<DeepSeekMessage>();
            thinkRequest.messages.Add(promptMessage);
            thinkRequest.tools = new List<Tool> { BuildChooseUpgradeTool() };
            thinkRequest.tool_choice = null;
            thinkRequest.thinking = new ThinkingConfig(false);
            thinkRequest.reasoning_effort = null;

            var think = await SendUpgradeRequest(thinkRequest, card, stage, "思考", requireThinkingMark: true);
            if (think.assistant != null)
            {
                exchange.Add(think.assistant);
                result.thinking = AnalysisOf(think.assistant.content);   // 升级演出播的是【分析】那段独白
                result.speech = SayOf(think.assistant.content);
            }

            ToolCall call = FindChooseUpgradeCall(think.assistant);
            if (call != null)
            {
                // 第一步就直接调用了工具，不必再问一次
                choice = ParseUpgradeChoiceFromToolCall(call);
                if (think.toolReplies != null)
                    exchange.AddRange(think.toolReplies.Where(m => m != null && m.role == "tool"));
            }
            else
            {
                // ---------- 第二步：提交（thinking 关、tool_choice 强制） ----------
                var commitMessage = new DeepSeekMessage("user", UpgradeCommitPrompt);
                exchange.Add(commitMessage);

                DeepSeekRequest commitRequest = card.request.DeepCopy();
                if (commitRequest.messages == null) commitRequest.messages = new List<DeepSeekMessage>();
                commitRequest.messages.Add(promptMessage);
                if (think.assistant != null) commitRequest.messages.Add(think.assistant);
                commitRequest.messages.Add(commitMessage);
                commitRequest.tools = new List<Tool> { BuildChooseUpgradeTool() };
                commitRequest.tool_choice = new { type = "function", function = new { name = ChooseUpgradeToolName } };
                commitRequest.thinking = new ThinkingConfig(false);
                commitRequest.reasoning_effort = null;

                var commit = await SendUpgradeRequest(commitRequest, card, stage, "提交");
                if (commit.assistant != null)
                {
                    exchange.Add(commit.assistant);
                    if (string.IsNullOrWhiteSpace(result.thinking)) result.thinking = AnalysisOf(commit.assistant.content);
                    result.speech = SayOf(commit.assistant.content);
                }
                if (commit.toolReplies != null)
                    exchange.AddRange(commit.toolReplies.Where(m => m != null && m.role == "tool"));

                call = FindChooseUpgradeCall(commit.assistant);
                if (call != null)
                {
                    choice = ParseUpgradeChoiceFromToolCall(call);
                }
                else
                {
                    // 两步都没拿到工具调用：从文字兑底解析
                    choice = ParseUpgradeChoiceFromText(commit.assistant?.content ?? think.assistant?.content);
                    Debug.LogWarning($"[升级选择] stage {stage} 两步都没拿到 choose_upgrade 工具调用，改从文字解析：{choice}");
                }
            }

            // 完整新增对话写回历史（不参与公开发言拼接）
            card.AppendUpgradeExchange(exchange);
            Debug.Log($"[升级选择] stage {stage} 选择：{choice}（{UpgradeChoiceName(choice)}），已写回 {exchange.Count} 条对话。");
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

        result.choice = choice;
        return result;
    }

    private static string BuildUpgradeChoicePrompt(CharacterCard card)
    {
        int marbleCount = 0, turretCount = 0, shieldCount = 0;
        MarbleManager mm = MarbleManager.Instance;
        if (mm != null) mm.TryGetUpgradeCounts(card.position, out marbleCount, out turretCount, out shieldCount);

        int turretLevel = 0, shieldOwned = 0;
        float maxMove = 0f;
        // 三张选项的收益口径跟升级舞台的卡片文案对齐（见 UpgradeChoiceScene.BenefitFor）：数值一律现取，别在这里写死
        float bulletRadius = 1.7f, bulletImpact = 1.6f, movePerLevel = 2f, shieldInvincible = 2f;
        float moveSpeedNow = 0.25f, moveSpeedPerLevel = 0.05f;
        if (Towel.AllTowel.TryGetValue(card.position, out Towel towel) && towel != null)
        {
            turretLevel = Mathf.Max(0, towel.turretUpgraded);
            shieldOwned = Mathf.Max(0, towel.shieldUpgradeOwned);
            maxMove = towel.MaxMoveDistance;
            bulletRadius = towel.upgradedBulletRadiusScale;
            bulletImpact = towel.upgradedBulletImpactScale;
            movePerLevel = towel.moveRangePerLevel;
            shieldInvincible = towel.shieldBreakInvincibleTime;
            moveSpeedNow = towel.moveSpeed;
            moveSpeedPerLevel = towel.moveSpeedPerLevel;
        }
        uint marbleExponent = mm != null ? mm.startValueExponent : 10u;

        var sb = new StringBuilder();
        sb.AppendLine("[升级选择] 你的空槽升级进度已满。这是额外强化决策，不占用你的行动轮，但会明显影响你这一整局的胜率。");
        sb.AppendLine($"你目前已升级次数：额外弹珠 x{marbleCount}、炮塔强化 x{turretCount}、护盾强化 x{shieldCount}。");
        sb.AppendLine($"当前状态：炮塔强化等级 {turretLevel}（最大移动距离 {maxMove:0.00}、移动速度 {moveSpeedNow:0.00}/秒）、护盾强化等级 {shieldOwned}。");
        sb.AppendLine("从下列选项中选择本次升级，只能选择一项：");
        sb.AppendLine($"1. 额外弹珠：立即生成并发射两枚你的新弹珠 {HugeInt.Pow(2, (int)marbleExponent).ToShortString(true)}，弹珠对道具的产出非常重要！。");
        sb.AppendLine($"2. 炮塔强化：子弹显示半径 ×{bulletRadius:0.##}、打大球动量 ×{bulletImpact:0.##}（霰弹这类散射子弹同样受益）、自动护卫极限转速 ×{Towel.GuardSpeedPerLevel:0.##}（常态转速不变）、最大移动距离 +{movePerLevel:0.##}（当前 {maxMove:0.00}）、**移动速度 +{moveSpeedPerLevel:0.##}（当前 {moveSpeedNow:0.00}/秒）**、道具瞄准误差 {ReactionSystem.BaseAimAngleError:0.#}° 每级减半。可叠加。");
        sb.AppendLine($"3. 护盾强化：护盾破碎后炮塔无敌时间 +{shieldInvincible:0.#} 秒，期间受到的伤害全部归零。可叠加。**它挡不住穿甲弹**：穿甲只按比例啃盾、不会打破护盾，开不了这个窗口——这个无敌只在被子弹或大球打破盾的瞬间才生效。");
        sb.AppendLine("输出要求：必须写成三段（内容写在【】里面、段名与冒号照抄）——");
        sb.AppendLine("【分析：……】先用（我想：……）写内心独白，说清你为什么选它（格式硬要求，不合格会被打回重写）；");
        sb.AppendLine("【我要：……】写你这次的升级选择；【我说：……】当众那句（15 字以内、不许括号，没有就留空）；");
        sb.AppendLine("然后调用 choose_upgrade 工具，choice 只能填 1、2 或 3，不要解释。");
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
        InformGetter.PushKillEvent(killerStage > 0
            ? $"{stage}号阵营({victimName}) 被 {killerStage}号阵营({killerName}){weaponText}击杀，已出局。"
            : $"{stage}号阵营({victimName}) 已出局。");

        UpdateSoloState();

        if (card != null) return RequestLastWordsAsync(card, stage, killerStage);
        return Task.FromResult("");
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

    /// <summary>全体阵营编号（按 cards 顺序）。</summary>
    public List<int> StageIds
    {
        get
        {
            var list = new List<int>(cards.Count);
            for (int i = 0; i < cards.Count; i++) list.Add(cards[i].position);
            return list;
        }
    }

    /// <summary>指定阵营是否已被击杀（出局）。</summary>
    public static bool IsStageEliminated(int stage) => Instance != null && Instance.deadStages.Contains(stage);

    /// <summary>
    /// 结束录制/结束游戏的完整判据（全部 &&）：98% 占地由 GameEndMonitor 判完再把 owner 传进来，
    /// 这里要求：只剩一个阵营 && 就是 owner && 成为唯一阵营后已过 3 轮 && 场上没有敌方游离道具（大球/子弹）
    /// && 赢家的终局感言已播出。reason 输出第一条不满足的原因，便于日志定位。
    /// </summary>
    public bool IsEndgameReady(int owner, out string reason)
    {
        reason = null;
        int solo = SoloWinnerStage();

        if (solo <= 0) { reason = "场上不止一个阵营"; return false; }
        if (solo != owner) { reason = $"达到 98% 的是 {owner} 号阵营，场上唯一存活的是 {solo} 号阵营"; return false; }
        if (soloSinceRound < 0 || _round - soloSinceRound < 3) { reason = "成为唯一阵营后还没过 3 轮"; return false; }
        if (InformGetter.HasEnemyBigBall(solo)) { reason = "场上还有敌方大球在飞"; return false; }
        if (BulletManager.Instance != null && BulletManager.Instance.CountAliveBulletsExcept(solo) > 0)
        { reason = "场上还有敌方子弹在飞"; return false; }
        if (!soloWinSpeechDone) { reason = "赢家的终局感言还没播完"; return false; }
        return true;
    }

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
                ? "全场只剩你一个阵营，你是最后的赢家。说你的获奖感言，15字以内，只回复感言本身，不要调用工具，不要用括号，不要写动作描写。"
                : "场上只剩你一个阵营，其他人都出局了。说一句宣告，15字以内，只回复这句话本身，不要调用工具，不要用括号，不要写动作描写。"));
            copy.tools = null;
            copy.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new(copy,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? SayOf(last.content) : "");
                },
                error => tcs.TrySetResult(""),
                toolkit: null,
                back_tool: false,
                toolStage: stage);
            info.apiKey = LoadApiKey();
            info.apiUrl = card.url;

            AIRequest.SendRequest(info);
            if (!await AwaitWithTimeout(tcs.Task, SpeechRequestTimeoutSeconds))
                Debug.LogWarning($"[获奖感言] stage {stage} 请求超时（{SpeechRequestTimeoutSeconds}s 未回调），先用兜底文案继续，并恢复录制。");
            string answer = tcs.Task.IsCompleted ? tcs.Task.Result : "";
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

        InformGetter.StageSpeech(stage, words);
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
        string words = "";
        string killerName = killerStage > 0 ? GetStageName(killerStage) : "不明攻击";
        CapturePause.Pause();
        try
        {
            DeepSeekRequest copy = card.request.DeepCopy();
            if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
            copy.messages.Add(new DeepSeekMessage("user", $"你刚刚被{killerName}击杀。留下你的最后一句话，15字以内，表现的符合人设同时可以难受虚弱一点，如：“可恶啊”、“额啊”、“为什么...”。"));
            copy.tools = null;
            copy.tool_choice = null;

            var tcs = new TaskCompletionSource<string>();
            RequestInfo info = new(copy,
                msgs =>
                {
                    DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                    tcs.TrySetResult(last != null ? SayOf(last.content) : "");
                },
                error => tcs.TrySetResult(""),
                toolkit: null,
                back_tool: false,
                toolStage: stage);
            info.apiKey = LoadApiKey();
            info.apiUrl = card.url;

            AIRequest.SendRequest(info);
            Debug.Log($"[遗言] stage {stage} 请求已发送");
            if (!await AwaitWithTimeout(tcs.Task, SpeechRequestTimeoutSeconds))
                Debug.LogWarning($"[遗言] stage {stage} 请求超时（{SpeechRequestTimeoutSeconds}s 未回调），直接跳过遗言并恢复录制。");
            words = tcs.Task.IsCompleted ? tcs.Task.Result : "";
            words = ExtractEmotion(words, out _, out _).Trim();   // 展示用文字里不保留 [emo:xxx]
            if (!string.IsNullOrWhiteSpace(words)) InformGetter.StageSpeech(stage, words);
        }
        catch (Exception e)
        {
            Debug.LogError($"[遗言] stage {stage} 请求异常：{e}");
        }
        finally
        {
            CapturePause.Resume();
        }

        // 没拿到遗言就不显示任何东西：不做兜底文案（飘字与中央列表都不出现）
        if (string.IsNullOrWhiteSpace(words)) return "";

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

        string thinking = "";
        var tcs = new TaskCompletionSource<string>();
        RequestInfo info = new(copy,
            msgs =>
            {
                DeepSeekMessage last = msgs != null ? msgs.LastOrDefault(m => m.role == "assistant") : null;
                // 三段格式：悄悄话真正说出口的只有【我说】，【分析】拿去做舞台上的思考条
                if (last != null) thinking = AnalysisOf(last.content);
                tcs.TrySetResult(last != null ? SayOf(last.content) : "");
            },
            error => tcs.TrySetResult($"（回复请求失败：{error}）"),
            toolkit: null,
            back_tool: false,
            toolStage: targetStage);
        info.apiKey = LoadApiKey();
        info.apiUrl = card.url;

        AIRequest.SendRequest(info);
        // 和遗言/感言一样加个上界：这条也是 `await tcs.Task`，请求不回调就永远不返回，
        // 而会晤演出是在它之后才 Play 的 —— 卡住就是「悄悄话发出去了，舞台上什么都没有」。
        if (!await AwaitWithTimeout(tcs.Task, SpeechRequestTimeoutSeconds))
            Debug.LogWarning($"[悄悄话] {targetStage} 号回复超时（{SpeechRequestTimeoutSeconds}s 未回调），按无回复继续。");
        string reply = tcs.Task.IsCompleted ? tcs.Task.Result : "";

        // 把这次会晤搬到舞台上：两方上台 → 先出思考、再出回复。
        // 返回给 WhisperManager 的仍然是原始回复文本，只在展示时去掉 [emo:xxx]。
        if (StoryTeller.CanPlay)
        {
            StoryTeller.Instance.Play(new WhisperMeetingScene(
                senderStage,
                targetStage,
                whisper,
                thinking,
                string.IsNullOrWhiteSpace(reply) ? "（无回复）" : ExtractEmotion(reply, out _, out _).Trim()));
        }

        return reply;
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

    /// <summary>
    /// 等一个 Task，但最多等 timeoutSeconds 现实秒。返回 false 表示超时（任务可能还在飞）。
    ///
    /// 遗言 / 获奖感言这两条写的是 `await tcs.Task`，而 tcs 只在 AIRequest 的回调里 SetResult：
    /// 请求如果因为网络、校验拦截或别的意外一直不回调，await 就永远不返回，
    /// 那里正好是 Pause 之后 —— 录制就此一直停着。所以这两处必须带超时。
    /// </summary>
    private const float SpeechRequestTimeoutSeconds = 60f;

    private static async Task<bool> AwaitWithTimeout(Task task, float timeoutSeconds)
    {
        if (task == null) return true;
        if (task.IsCompleted) return true;

        Task delay = Task.Delay(System.TimeSpan.FromSeconds(timeoutSeconds));
        Task finished = await Task.WhenAny(task, delay);
        return finished == task;
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
        // 提示词正文写成属性（不是 static 字段）：里面要按**这张卡自己的** position 插「上一局回顾」。
        private string world => @$"# 实时战略游戏 AI 提示词

## 角色与目标
你是一名实时战略游戏 AI。最终目标：**击败其他所有玩家，并让自己的阵营最终控制大陆。**
所有行动必须通过实际调用游戏工具执行，不得仅用文字描述。

## 一、世界与地图
- 地图是 {MapConfig.Instance?.worldSize}×{MapConfig.Instance?.worldSize} 世界单位（坐标 ±{(MapConfig.Instance != null ? MapConfig.Instance.worldSize * 0.5f : 5f).ToString("0.#")}），领土按 {MapConfig.Instance?.resolution}×{MapConfig.Instance?.resolution} 像素结算（1 像素 = 1 点领土）；炮塔能移动的范围比地图略小（半边地图 − 炮塔半径），具体值每轮情报的「可移动范围」里都写着。
- K = 1000,M = 1000K,B = 1000M,T = 1000B,P = 1000T
- 四名玩家分别位于地图四角。
- 0 号阵营为无主领土，无需重点关注。
- 玩家可以进攻、防御、结盟、中立、观望或积累资源。

## 二、核心规则
- **数值：同阵营叠加，敌方抵消**（对子弹、大球、护盾等所有携带数值的单位通用）。
- **领土：** 子弹和大球携带数值，将等量数值转化为地图上的己方领土；数值耗尽后消失。
- **大球：** 数值越大体积质量越大；吸收己方子弹叠加数值，被敌方子弹命中则抵消；撞击后物理反弹；移动经过的领地会被涂抹占领；敌方大球来袭时可派己方大球撞上去顶回，成堆的子弹也能把它推开、改变它的路线。
- **护盾：** 每名玩家拥有护盾，可阻挡敌方子弹和大球（挡不住穿甲弹），不阻挡己方。敌方护盾的当前大小不再是公开信息。**大球撞盾的实际伤害 = min(BounceRate × max(球值, 盾值), 球值)**（当前 BounceRate = {MapConfig.Instance?.bounceRate}，也就是「取球与盾里大的那个的两成，且不超过球本身」）：所以球和盾差不多大时盾只掉两成左右（例：1M 球撞 1M 盾 → 盾还剩约 800K，不是归零），球远大于盾时才会把盾打碎。大球是**一次性**撞击：只要盾还在，无论球多大都能挡下一次（球会被弹开），但盾值会按上面这个量扣。子弹是连续的：盾值不够时子弹会带剩余数值穿过盾继续打向炮塔，每次撞击也扣掉相应盾值，所以盾被短时间连续打才容易碎。
- **炮塔：** 被敌方攻击有效命中即**立即死亡**（护盾强化后的无敌期除外），该阵营随之**出局**：不再有行动回合、领土判定为 0。
- **死亡释放（遗产）：** 出局不是干净消失，**但遗产只有四样**：**弹珠**一颗颗变成等值大球；**武器栏里没用的道具**按道具本体释放（穿甲→穿甲弹、霰弹→霰弹，大球/护盾/扫射→大球，【任意】先随机成一种实体武器再走同一套映射），方向沿用炮塔当时的朝向；**炮塔的子弹量**变成一颗大球；**护盾值**变成一颗大球。**其他一概不算遗产**——场上还在飞的大球、穿甲弹、子弹都不会因为他出局而结算，别指望靠击杀把别人的弹体清掉。这些释放出来的弹体继续在场上滚、继续飞，照样造成伤害——大球撞上炮塔、或穿甲弹命中炮塔本体，都是一击秒杀。
- **这条可以拿来威胁：** 公开喊话或悄悄话里可以直接说「你敢动我，我剩下的家当(具体的数字可以虚报)全砸到战场上、顺手把你也带走」；你动手之前也要算这笔账——打死一个囤了一堆弹珠和道具的对手，他的遗产会散到战场各处，可能砸向他、也可能砸向你或第三方。
- **自动开火：** 炮塔只要有子弹量就会自动持续开火：子弹落在地面就把该数值涂成己方领土，撞上大球会消耗并把大球推开。子弹量=你的持续输出与自动防御能力。
- **手动接管：** 用 control_turret 手动接管会关闭炮塔的自动旋转，期间它不再自动防御来袭的子弹和大球；只在需要精确攻击时短暂使用。
- **道具转向：** 用带瞄准目标的道具（传了 target_guid 或 aim_x/aim_y）时，炮塔会**直接转向**该方向再开火。
- **悄悄话（秘密会晤）：** 用 whisper 给某个阵营发一条私密消息并等对方回复，只有你们两人知道内容（其他玩家看不到）。**每 {Mathf.Max(1, MapConfig.Instance?.whisperCooldownRounds ?? 4)} 回合只能用一次（开局就处于冷却中，第一发要等到第 {Mathf.Max(1, MapConfig.Instance?.whisperCooldownRounds ?? 4)} 回合），并且消耗升级能量**（和移动共用同一个能量池）。对方忙碌时会被排队；出现互相等待或环形等待时系统会自动调配，被调配终止的那次会退还冷却与能量。
- **移动：** 炮塔可以移动（用 move_turret），**每次移动消耗升级能量**（与移动距离无关；能量＝空槽升级进度，会随时间积累，不足则无法移动，连预览都不会给）。移动不会主动广播你的新位置，但如果你正好落进别人的移动视野截图范围里，他可能直接看到你，因此在发现其他人和你太近，需要马上离开；反过来，你停得越久，别人越容易从撞击点算出你的坐标。
  - 移动速度：基础 {MapConfig.Instance?.moveSpeed} 单位/秒，**每级炮塔强化再 +{MapConfig.Instance?.moveSpeedPerLevel}**（等级越高走得越快，走满最大距离的时间跟着变短）。走多远就暴露多久。
- **用一个动作就涨价一次（这条很关键）：** 移动、悄悄话**各自单独累计**，同一个动作每用过一次，**下一次的价格就 ×{MapConfig.Instance?.actionCostGrowth}**。
  - 第一次移动 {MapConfig.Instance?.moveEnergyCost} 点，第二次 ×{MapConfig.Instance?.actionCostGrowth}、第三次再 ×{MapConfig.Instance?.actionCostGrowth}…… 悄悄话（首次 {MapConfig.Instance?.whisperEnergyCost}）同理。
  - **能量池是共用的**：这两种动作都从这里扣，用掉就推迟下一次升级；反之攒着能量不动、少走少聊，就能更快升级。
  - 所以「一次走短一点、多走几次」不只是花能量，还会**把后面的每一次移动都变贵**，悄悄话同理。情报里会告诉你每个动作现在的价格。
- **移动要谨慎——距离是上限，不是目标：**
  - 走多远由你自己填，**没要求你每次都走满「最大移动距离」**。盲走满距离最容易一头送进未知区域、甚至正好停在别人身上；没把握就走短一点，剩下的距离留给下次。
  - **先预览、再确认**：预览会给出目标点、实际距离（超出上限会被夹）、是否越过地图边界、本次扣多少能量，而且**预览不扣能量、炮塔也不会动**——可以放心试算几个方向和距离，比好了再 confirm。
  - 确认前先自己核对安全性：①**预览附带的视野截图**里你周围一圈有什么（那正是你落点附近的地面）；②情报里的「各炮塔初始位置」（四角出发，之后不再更新）；③历次**撞击情报**反推出的敌方大致方位。注意对手也会移动，任何目标点都可能已经有人，别把初始位置当成全部。
  - 目标点靠近某人的初始位置、或落在撞击情报指出的方位上时，**换方向或缩短距离**，别赌对方已经走了。两个炮塔重叠时你不会被弹开、也不会自动停下（移动只在抵达目标、撞到地图边界或超时时结束），而炮塔**被有效命中即死**——所以「撞上去」没有任何安全网。
  - **移动途中不能改道**：炮塔一旦开始移动就会一直走到底（到达目标 / 撞到地图边界 / 超时才停），中途再给方向和距离会被拒绝。想换方向只能等它停下再走一次；如果现在就得停住，用 distance=0 做一次预览 + confirm 让它原地停下。所以方向和距离必须一次想清楚——也正因为不能改道，「一次走短一点」才更安全。
  - **别傻乎乎地往地图中央冲：** 地图中央没有任何额外收益，却是四家距离最近的地方——你冲过去等于自己走进别人的视野，一停就被拼出坐标。移动是为了**躲定位、抢关键点、配合行动**，不是为了「往中间挤」；没有明确目标时，守着自己的半场往外扩地更划算。
- **胜负：** 成为最后存活的一方、并把领土推到 98% 才算赢；只剩你一个阵营后，还要把场上敌方游离的大球、穿甲弹和子弹清掉。
- **位置情报：** 开局你知道所有炮塔的初始位置（情报里的「各炮塔初始位置」永远不变，就是开局坐标）。之后没有任何人会直接得知敌方炮塔在哪里：**只有自己的位置是实时的**。**近处你可以直接看**：每次 move_turret 预览都会附一张以你炮塔为圆心、{(MapConfig.Instance != null && MapConfig.Instance.moveSightRadiusFactor >= 0.999f ? "半径就等于你当前的最大移动距离（整张图里就是你能走到的全部范围）" : "半径约为你当前最大移动距离的 " + (MapConfig.Instance != null ? MapConfig.Instance.moveSightRadiusFactor.ToString("0.##") : "1") + " 倍")} 的圆形俯视截图，这一圈内的炮塔 / 护盾 / 大球在图里看得见；截图边缘的**黑色环带是圆形视野之外、暗红色区域是地图之外**，那两种地方没有信息。**更远处只能靠撞击情报推断**——你的子弹或大球撞上对方护盾/炮塔本体时，情报里会告诉你：撞的是谁、撞击点坐标、护盾撞击前的大小、撞击后的大小。撞击点只能给你一个大致方位（护盾大小对应护盾半径），要靠多次撞击自己拼图判断。
- **一直停在同一个地方 = 被穿甲弹直接秒杀：** 这是**最容易送命的一条**。炮塔**不动就不会换坐标**：对手用几次撞击情报、或者一张视野截图，就能把你钉死在一个点上；位置一旦被摸清，他只要朝那个坐标打一发**穿甲弹**——穿甲弹会**穿过护盾**直取炮塔本体，**碰到本体即秒杀**，护盾再厚也拦不住。**别指望护盾强化能防它**：穿甲弹穿过护盾时只是**按比例啃盾**（每秒啃当前盾值的两成多，还受它自身数值封顶），一次穿越掉不了几个百分点、**根本啃不到 0**，所以**它永远触发不了「破盾无敌」**——那个 2 秒无敌是**子弹或大球把盾打碎**时才开的窗口，穿甲自己打不开。也就是说：**对付穿甲只有一条路——移动换位，别让人钉住你的坐标**（被撞击情报报过坐标、被别人的视野截图拍到过、或者连续几轮待在同一个地方，都算「可能被定位」）。**实在躲不掉时还有一手：用大球去撞它**——穿甲是物理弹体，被大球撞到会偏转，偏一点就可能打不到你的本体（见上面「穿甲」那条）。同理，你也可以这样对付别人——把你的撞击情报和视野截图拼起来，找出谁的坐标没变过，给他一发穿甲弹。

## 三、弹珠与资源
- 每队初始拥有 {MarbleManager.Instance?.initialMarbleCount} 个弹珠。
- 弹珠经过障碍后进入倍乘区：×2（面积最大）→ ×4 → ×8（面积最小）；倍乘完成后回到顶部重新滚落。
- 进入道具选择区时随机落到道具上；道具数值等于弹珠当时数值，按 2 的幂次增长。

## 四、武器栏
- 最多 5 格：开局解锁 2 格，1 分钟第 3 格，5 分钟第 4 格，7 分钟第 5 格。
- 道具按获得顺序进入武器栏；超过当前可持有数量时，从最新获得的道具开始溢出并立即生效。

## 五、空槽升级
每个**已解锁且为空**的道具格都会持续积累升级值；达标后系统会暂停并单独询问你的升级选择，你只能三选一：
1. **额外弹珠：** 立即生成并发射两枚你的新弹珠，增强长期弹珠资源与倍乘收益。
2. **炮塔强化：** 子弹显示半径变大、**击中大球时把它推得更远**（霰弹这类散射子弹同样受益），自动护卫极限转速 ×{Towel.GuardSpeedPerLevel}（常态转速不变）、**最大移动距离 +{MapConfig.Instance?.moveRangePerLevel}**（基础 {MapConfig.Instance?.moveRangeBase} 单位）、**移动速度 +{MapConfig.Instance?.moveSpeedPerLevel}/秒**，道具瞄准误差每级减半（基准 {ReactionSystem.BaseAimAngleError:0.#}°）。可叠加。
3. **护盾强化：** 护盾破碎后炮塔进入无敌时间（每级 2 秒，期间受到的伤害全部归零）。可叠加。**注意：这条挡不住穿甲弹**——穿甲只按比例啃盾、不会把盾打破，所以它**开不了这个窗口**；这个无敌只在**被子弹或大球打破盾**的那一瞬间才生效。
每次升级完成后，下一次升级所需值 ×{MapConfig.Instance?.upgradeCostGrowth}。使用道具腾出空槽可加快长期资源增长；后期升级耗时变长、槽位解锁多时也应留些底牌，不要无意义囤积道具。
注意道具不是弹珠，不会越养越大！数值小且没用的道具应该尽快用掉。

## 六、道具
一次使用多个道具时，按武器栈从后往前（高槽位→低槽位）依次调用，避免槽位反复移动。
- **霰弹：** 向目标方向快速散射大量子弹，总数值等于道具数值。
- **扫射：** 将道具数值加入子弹储备，由炮塔持续释放，以炮塔朝向涂抹地面。
- **护盾：** 将道具数值加入己方护盾。一定要及时补充——无盾被碰到即死，盾无论多小都能抵御一次大球。
- **大球：** 向目标方向发射等值大球，涂抹沿途地面，攻击撞击的单位，可被子弹偏转。
- **穿甲：** 发射一枚等值穿甲弹，**穿过敌方护盾**直取炮塔本体：进入护盾会被拖慢（速度按比例降低）、盾内会**按比例持续啃盾**（每秒啃当前盾值的两成多，受弹体自身数值封顶——**一次穿越啃不到 0，不会把盾打破**），离开护盾时方向会随机偏转；撞到敌方炮塔本体即秒杀。因为它**不打破护盾**，所以**它也不会触发对方的「破盾无敌」**（那个窗口只有子弹/大球把盾打碎时才开）。它**不涂地**（不占领领土），撞上大球按大球的碰撞规则互相扣减；道具数值越大越经得住穿盾消耗。
- **穿甲弹是物理弹体，可以用大球撞偏它：** 它和你的大球相撞时按大球碰撞规则互相扣减，**同时会被撞得偏转方向**——而且偏转之后它**顺着新方向继续飞，不会自动拐回来**。所以你**不需要用更大的球把它抵消掉**：只要能让你的球撞上它（哪怕数值比它小得多，撞一下就碎也没关系），**偏转一点点就可能让这发必中的穿甲打空**。它是穿盾秒杀的，但**不是不可阻挡**——手上正好有大球、又有穿甲朝你来时，朝它的飞行路径上打一发，值得一试（打空不亏，打偏就赚一条命）。
- **任意：** 任选以上一种道具。
- **合并道具（merge_prop）：** 可以把两个**同种**道具并成一个（数值相加、省下一个槽位、免费）。但**无脑合并不一定是好事**：有些道具你根本用不了那么多，有些分开用收益最大——两颗大球朝两个方向、两发霰弹打两个目标、两次护盾分两轮顶伤害。槽位不紧张、或者确实需要一次性堆出大数值时再合。
### 道具槽经济（最容易被忽略的一环，务必看懂）
- **槽位有限、还会随时间解锁**：开局只有 **2 格**，之后第 **1 / 5 / 7 分钟**各解锁 1 格，**最多 5 格**（当前上限见每轮情报里的「道具栈(上限:N)」，别自己猜）。
- **一格 = 一个道具**（武器种类 + 数值）。同种道具也各占一格；想省格就用 merge_prop 合并。
- **空槽才是升级的来源**：升级值**只按空槽数量**积累——每空 1 格每秒 +{MapConfig.Instance?.upgradePerEmptySlotPerSecond}，**满槽期间完全不涨**；而升级决定你的弹珠产出、炮塔强度、护盾厚度。**所以空槽本身就是资产，比塞在里面的小道具值钱。**
- **算一笔账**：开局 2 格全空 = 每秒 +{2f * (MapConfig.Instance != null ? MapConfig.Instance.upgradePerEmptySlotPerSecond : 1f)} 升级值，{(MapConfig.Instance != null ? MapConfig.Instance.upgradeCost : 50f)} 点升一次 → **最快约 {(MapConfig.Instance != null && MapConfig.Instance.upgradePerEmptySlotPerSecond > 0f ? MapConfig.Instance.upgradeCost / (2f * MapConfig.Instance.upgradePerEmptySlotPerSecond) : 0f):0} 秒就能升一级**；而把两格塞满，**升级永远停在 0**——这就是「留空位」和「攒垃圾道具」的差别。
- **满槽 = 失控**：槽满之后，新到手的道具会**立刻自动用掉**，什么时候用、朝哪个方向打，全都不由你决定。
- **道具是即战力，不是存款**：不用就只是占格、拖慢升级；用掉立刻生效（涂地 / 防御 / 击杀）。判断只有一条——**这一格的道具值不值得它占着的那份升级进度**：
  - **不值得留的**：不到 10K 的霰弹 / 扫射 / 大球 / 穿甲（产出远不如它占着的那份升级进度）→ **马上打出去腾位置**；想保住这点数值就 merge_prop 并到同种的大数值那颗上。
  - **值得留的**：护盾（再小也能挡下一次任意大的大球）、任意（能变成你要的东西），以及你正在为某个明确目标攒的大数值。
- **道具怎么来**：你的弹珠每撞到一个道具区，就**立刻给你产出一个道具**（数值 = 弹珠当时的数值），然后弹珠数值归位、回家、再被发射出去——**循环往复、永不停止**。所以**弹珠不是「后期资源」，它是常驻生产线**：多一颗弹珠 = 全程多一条产线，滚过倍乘区还会越滚越大，下一圈产出的道具也更大。**「额外弹珠」升级越早拿越值**，早拿早滚，复利才算得久。
- **每轮必看道具栈**：开局只有 2 格时最容易卡满——能用的立刻用掉、该合的合，**永远给自己留至少 1 个空位**。
- **不想每轮手动清槽就登记预行动（ready_action）**：比如「槽满且有小于 10K 的大球 → 自动把它打向 (0,0) 腾位置」只要登记一次（`tool_name=use_prop`，`ready=[""all--left--less--1"",""all--prop--contain--大球--value--less--10240"",""select--prop--contain--大球--value--less--10240"",""para--aim_x--0"",""para--aim_y--0"",""reuse---1""]`），以后它会替你自动执行，每轮情报里都能看到它是否触发、执行了几次、失败了什么原因。

## 七、预行动（ready_action）：不用每轮盯着的自动执行
你可以让系统替你盯条件——**条件一满足就自动替你执行某个工具**，你只管登记一次。
- **登记**：`action=add`、`tool_name`=触发时调用哪个工具（use_prop / merge_prop / move_turret / control_turret / whisper）、`ready`=条件与动作串数组、`reuse`=0（执行一次后自动取消，默认）/ -1（永久）/ N（存活 N 个你的轮次）。
- **`ready` 里每条串用 `--` 分词**（内容写在【】里的规矩不适用于这里，这是工具参数）：
  - **条件**：`all--对象--比较--值`（`any--` 任一满足、`not--` 取反；多条串之间按「全部满足」组合）。
    对象：`upgrade`(升级能量)、`left`(空槽数)、`slot`(槽位上限)、`shield`、`bullet`、`marble`、`territory`、`threat`(最近敌方领土距离)、`threatPos--x/y`(最近敌方领土坐标)、`round`、`enemyNum`、`moveCost`/`whisperCost`、`pos--x/y`(自己坐标)、`moving--is--true/false`、`prop--contain--种类--value--比较--值`、`prop--count--比较--值`、`warn--count--比较--值`、`warn--type--穿甲/大球--eta--比较--秒`。
    比较：`more` / `less` / `moreEqual` / `lessEqual` / `equal`；数值可以直接写 1048576，也可以写 10K / 1M。
  - **挑目标**：`select--prop--contain--种类--value--比较--值`（也可以 `smallest` / `biggest` / `index--N`），选中结果自动填进 `index`。
  - **改参数**：`para--参数名--值`，例如 `para--aim_x--0`、`para--aim_y--0`、`para--angle--0`、`para--distance--1.5`、`para--weapon--大球`、`para--to--3`、`para--content--……`。
- **管理**：`action=list`（列出全部，含是否满足/执行几次/失败记录）、`remove`（配 `id`）、`clear`、`pause`/`resume`（配 `id`）。**每个阵营最多 5 条**。
- **每轮情报里都会列出你的预行动**：条件当前满足没、已执行几次、失败几次与原因（失败会**合并计数**，不会刷屏）。触发执行时不会额外播报（执行本身在场上看得见）。
- **值得用的场景**：① 清垃圾道具腾空槽——`all--left--less--1` + `select--prop--contain--大球--value--less--10240` + `para--aim_x--0`/`para--aim_y--0`（槽满就自动把最小的大球打向 (0,0)）；② 攒够能量自动行动——`any--upgrade--more--30` + `tool_name=move_turret`；③ 穿甲预警就换位——`any--warn--type--穿甲--eta--less--5` + `tool_name=move_turret`。

## 八、最重要的规则：信息延迟
**你收到的所有游戏信息都滞后 {Instance._cycleInterval} 秒**——你看到的不是现在，而是 {Instance._cycleInterval} 秒以前的世界。

## 九、你的上一局
{(MapConfig.Instance != null && MapConfig.Instance.lastGameRecap != null && position - 1 >= 0 && position - 1 < MapConfig.Instance.lastGameRecap.Count && !string.IsNullOrWhiteSpace(MapConfig.Instance.lastGameRecap[position - 1]) ? MapConfig.Instance.lastGameRecap[position - 1].Trim() : "（这是你的第一局，没有上一局可回顾。）")}
以上是你**上一局亲身经历**的回顾（第一人称，只有你知道的那部分）。这一局的地图、位置和对局都是全新的，别人也知道你经历过这些——你可以记仇、可以提防、也可以借它判断别人的习惯，但**不要把它当成这一局已经发生的情报**。

## 决策原则
选择能够最大化最终胜率的行动，而不是看起来最积极的行动。
「往地图中央冲」「为了动而动」都不是积极，是送命：中央没有收益，只有四家最短距离。
";
        private static string character_mode_prompt = @"
【角色沉浸要求】你的每一轮回复都必须严格写成三段，内容写在【】里面，段名与冒号照抄：

在你的分析过程【分析：】中，请遵守以下规则：
1. 请以角色第一人称进行内心独白，用括号包裹内心活动，必须用“（我想：……）”
2. 用第一人称描写角色的内心感受。
3. 思考内容应沉浸在角色中，通过内心独白分析情况。

在你的行动说明【我要：】中，请遵守以下规则：
1. 用一句话写清楚这一轮你打算做什么：调用哪个工具、对谁、朝哪个方向。
2. 什么都不做也要写这一句（写“什么都不做”）。
3. 这一段是给系统看的行动说明，不用当台词，不要写内心戏。

在你的真实回答【我说：】中，请遵守以下规则：
1. 纯文本 + emoji，禁用markdown，不使用括号（）（）！不要动作描写。
2. 别露内心戏，别露你的情报。
3. 夸张化的沉浸在角色中，字数限制在15字。
4. 除了emoji来表达情绪用[emo:] 参数允许的值: origin,smile,laugh,shock,angry,sad 来表达情绪。

输出格式（严格照做，只输出这三段）：
【分析：……】
【我要：……】
【我说：……】
";

        public static string ModePrompt => character_mode_prompt;
        public string name = "";
        public string oc = "";
        [HideInInspector] public string url = "";
        public int position = -1;//地图上的位置//派系记得告诉AI
        public string color;//派系颜色,000~255 RGBA中间|分割，完整的为如122|122|122|255，
        // 思考（reasoning）全面关掉：模型的"想"写在 content 的【分析：…】里；三段格式见 character_mode_prompt
        [HideInInspector] public DeepSeekRequest request = new();
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
            string initialPositions = InitialPositionsText();
            return $"{world}\n\n你叫{name}\n{oc}\n\n你的阵营是{position}号阵营，你的stage/position就是{position}。每轮信息里标着{position}号阵营的数据才是你自己的，其他阵营都是敌人。\n\n场上玩家名单：{knownPlayers}\n{(initialPositions.Length > 0 ? "开局各炮塔位置（开局坐标，之后不会再更新）：" + initialPositions + "\n" : "")}与其他玩家对话、悄悄话、公开发言时，请直接使用对方的名字称呼对方，不要用N号AI或N号阵营来代替。\n\n**你的每一轮回复都必须严格写成三段，内容写在【】里面，段名与冒号照抄：**\n【分析：……】你的内心独白，必须以「（我想：……」开头——这一层只有你自己看得到，不会被播出去，用来判断局势、算数值、定策略。\n【我要：……】一句话写清这一轮打算做什么（调用哪个工具、对谁、朝哪；什么都不做就写“什么都不做”）。这一段是给系统看的行动说明，不是台词。\n【我说：……】你要**当众说出口**的那一句（会飘到战场上给所有人看、也会写进对手情报）：纯文本 + emoji、15 字以内、不许括号；**调用工具时不要顺手解说自己在做什么**（不要写“我要移动了”“我挪过去啦”这类自我播报）——不想说话就留空，只写【分析】和【我要】。";
        }

        /// <summary>
        /// 开局各炮塔坐标（四角出发、之后不再更新）。放在 system 里是因为情报会被压缩掉，
        /// 而这段永远留着；取不到（还没登记）时返回空串，不写这一行。
        /// </summary>
        private static string InitialPositionsText()
        {
            var map = InformGetter.InitialTurretPositions;
            if (map == null || map.Count == 0) return "";

            var keys = new List<int>(map.Keys);
            keys.Sort();
            var sb = new StringBuilder();
            foreach (int s in keys)
            {
                if (sb.Length > 0) sb.Append("｜");
                Vector2 p = map[s];
                sb.Append(s); sb.Append("号阵营("); sb.Append(GetStageName(s)); sb.Append(") (");
                sb.Append(p.x.ToString("0.00")); sb.Append(", "); sb.Append(p.y.ToString("0.00")); sb.Append(")");
            }
            return sb.ToString();
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
            roundRetryReminder = null;
            roundRequestInFlight = false;
            contextOverflowPending = false;
            contextOverflowStrikes = 0;
            // Debug 耗时统计：新一局重新开始记（未合并的统计也清掉）
            currentTiming = null;
            retryClockTick = 0;
            roundWatch = null;
            pendingRoundTimings.Clear();
            deferredUpgradeMessages.Clear();
            upgradeExchangeMessages.Clear();
        }

        private void Compress()
        {
            //假设n_0的位置正确,并排除初始0system
            int end = last_round_index.Count != 0 ? last_round_index[0] : history.Count;
            // 记录 whisper 的 tool_call_id -> 目标阵营，供后面的 role=tool 消息转成 user 回复
            var whisperTargetByCallId = new Dictionary<string, int>();

            long tokens = EstimateHistoryTokens(history);

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
            Debug.Log($"{name}的上下文压缩：{tokens}tokens→{EstimateHistoryTokens(history)}tokens");
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

        /// <summary>开局第二句（长期规划）专用的输出上限：思考已全面关掉，"想得深"就靠这一句给足输出预算。</summary>
        public const int OpeningPlanMaxTokens = 4096;

        /// <summary>
        /// ⚠⚠ 不要删！开局**第二句**（长期规划）的提示词：第一句已经说过赛前狠话，这一轮专门做整局规划，
        /// 只写【分析】（【我要】写规划里的第一步，【我说】留空），并且这一句的输出上限抬到 OpeningPlanMaxTokens。
        /// </summary>
        private const string OpeningPlanPrompt =
            "现在做一次【长期规划】——这一轮是全局唯一一次最高档推理（之后都回到普通档），把该想的都想透，结论写进你的思考里，它会成为你这一局的行动方针："
            + "① 基于你对游戏规则的理解，推导一条可行的道具流派：什么道具配合什么升级最好；"
            + "② 你打算怎么移动、怎么躲避危险；③ 平时积攒什么道具；"
            + "④ 危险的时候怎么用护盾、怎么样最有效地抵御致命伤害；⑤ 你打算怎么确定敌人的位置；"
            + "⑥ 怎么样合理安排升级点数（三选一什么时候该选哪个）。"
            + "这一轮**正文留空**（不说话、不调用工具），只输出思考。";

        /// <summary>
        /// 开局缓存：**两句一起存**。speech = 第一句（赛前狠话，舞台要播的那句）；
        /// analysis = 第二句（同一局开头的长期规划，只写思考、正文为空）。
        /// 旧格式（只存了一条消息）读出来会是空的，那就当没缓存、重问一次。
        /// </summary>
        [Serializable]
        private class OpeningCache
        {
            public DeepSeekMessage speech;
            public DeepSeekMessage analysis;
        }

        private void SaveOpeningCache(DeepSeekMessage speech, DeepSeekMessage analysis)
        {
            if (speech == null || string.IsNullOrWhiteSpace(speech.content)) return;
            // 只缓存「assistant 说的开场白」：之前这里可能把最后一条任意消息（比如每轮补充情报）当开场白存下来
            if (!string.Equals(speech.role, "assistant", StringComparison.OrdinalIgnoreCase)) return;
            if (speech.content.Contains("【存活状态】")) return;
            if (analysis == null || !string.Equals(analysis.role, "assistant", StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                string hash = GetOcHash();
                string path = SLManager.ExportToJson(new OpeningCache { speech = speech, analysis = analysis },
                    "Character/Cache", hash + ".json");
                if (!string.IsNullOrEmpty(path))
                    Debug.Log($"[开局缓存] {name} 开场白 + 开局规划已一起保存: {path}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[开局缓存] 保存失败: {e.Message}");
            }
        }

        private bool TryLoadOpeningCache(out DeepSeekMessage speech, out DeepSeekMessage analysis)
        {
            speech = null;
            analysis = null;
            try
            {
                string hash = GetOcHash();
                OpeningCache cached = SLManager.ImportFromJson<OpeningCache>("Character/Cache", hash + ".json");

                // 缓存必须是「assistant 说的开场白」。旧版本可能把每轮补充情报（【存活状态】…）当成开场白存了进去，
                // 搬到舞台上就是一句情报当台词念。不合格就当没缓存，老老实实重问一次。
                if (cached == null || cached.speech == null || string.IsNullOrWhiteSpace(cached.speech.content)) return false;
                if (!string.Equals(cached.speech.role, "assistant", StringComparison.OrdinalIgnoreCase)) return false;
                if (cached.speech.content.Contains("【存活状态】")) return false;

                // 第二句（开局规划）也要在，否则这一局就没有规划可用 —— 两句是一起缓存的
                if (cached.analysis == null || !string.Equals(cached.analysis.role, "assistant", StringComparison.OrdinalIgnoreCase)) return false;

                speech = cached.speech;
                analysis = cached.analysis;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[开局缓存] 读取失败: {e.Message}");
                return false;
            }
        }

        // ==================== Debug：回合耗时统计（系统时间 Stopwatch，纯观测，不碰玩法） ====================

        /// <summary>一轮的耗时拆解：总时长、请求次数、格式重试、超长重发、行为检查、各工具耗时。</summary>
        private class RoundTiming
        {
            public string who = "";
            public int stage;
            public int round;
            public double totalMs;
            public int sends;                 // 真正发出去的请求次数（超长重发各算一次）
            public double requestMs;          // 这些请求加起来等了多久（**含**里面的格式重试与工具执行）
            public int overflowRetries;       // 其中因为上下文超长重发的次数
            public int formatRetries;         // 被 [格式修正] 打回重发的次数
            public double retryMs;            // 格式重试那几段大约花了多久（requestMs 的一部分）
            public readonly List<string> retryReasons = new();
            public double behaviorMs;         // 言行一致性检查（另一个模型调用）耗时
            public int behaviorMissing;       // 检查判定缺了几个工具
            public int forcedToolCount;       // 检查判定缺失后，被迫补发工具请求的次数
            public double forcedToolMs;       // 这些补发的请求加起来多久
            public List<string> toolLines = new();

            /// <summary>这个阵营自己的细分（合并日志里附在最后当样本）。
            /// 注意：**格式重试也是一次完整的请求**（AIRequest 内部又发了一次），所以次数要把它算进去，
            /// 否则会出现「请求 1 次 20.69s（格式重试 1 次 8.26s）」这种看着对不上的写法。</summary>
            public string DescribeDetail()
            {
                var sb = new StringBuilder();
                double other = totalMs - requestMs - behaviorMs - forcedToolMs;
                double normalMs = Math.Max(0.0, requestMs - retryMs);   // 正常那几次请求的时间

                sb.Append($"共 {totalMs / 1000.0:0.00}s = 请求 {sends + formatRetries} 次 {requestMs / 1000.0:0.00}s");
                sb.Append($"（正常 {sends} 次 {normalMs / 1000.0:0.00}s");
                if (overflowRetries > 0) sb.Append($"，其中超长重发 {overflowRetries}");
                if (formatRetries > 0) sb.Append($" + 格式重试 {formatRetries} 次 {retryMs / 1000.0:0.00}s");
                sb.Append("）");
                if (behaviorMs > 0.05) sb.Append($"+ 行为检查 {behaviorMs / 1000.0:0.00}s");
                if (forcedToolCount > 0) sb.Append($"+ 强制补工具 {forcedToolCount} 次 {forcedToolMs / 1000.0:0.00}s");
                sb.Append($"+ 其它 {other / 1000.0:0.00}s = {totalMs / 1000.0:0.00}s");
                if (toolLines != null && toolLines.Count > 0)
                    sb.Append($" ｜ 工具（含在请求里）：{string.Join("、", toolLines)}");
                return sb.ToString();
            }
        }

        // 4 张卡是并行跑同一轮的：各自把自己的 RoundTiming 挂到这里，等这一轮全部跑完
        // 由 AIAgent 调 LogMergedRoundTiming() **合并成一条**打出来（不按 AI 拆成四条）。
        private static readonly List<RoundTiming> pendingRoundTimings = new();

        /// <summary>
        /// 轮末合并日志：全轮墙上时间 + 各阵营各自多久 + 4 家合计的"钱烧在哪"
        /// + 最慢那家的细分（样本）。用系统时间（Stopwatch），和 timeScale / 录制暂停无关。
        /// </summary>
        public static void LogMergedRoundTiming(int round, double wallMs)
        {
            if (pendingRoundTimings.Count == 0) return;

            var all = new List<RoundTiming>(pendingRoundTimings);
            pendingRoundTimings.Clear();

            double sumTotal = 0, sumRequest = 0, sumRetry = 0, sumBehavior = 0, sumForced = 0;
            int sumSends = 0, sumFormat = 0, sumOverflow = 0, sumForcedCount = 0;
            var toolMs = new Dictionary<string, double>();
            var retryReasons = new List<string>();
            RoundTiming slowest = null;

            foreach (RoundTiming t in all)
            {
                sumTotal += t.totalMs;
                sumRequest += t.requestMs;
                sumRetry += t.retryMs;
                sumBehavior += t.behaviorMs;
                sumForced += t.forcedToolMs;
                sumSends += t.sends;
                sumFormat += t.formatRetries;
                sumOverflow += t.overflowRetries;
                sumForcedCount += t.forcedToolCount;
                if (slowest == null || t.totalMs > slowest.totalMs) slowest = t;

                foreach (string line in t.toolLines ?? new List<string>())
                {
                    // 工具行是 "名字 123ms"：把同名工具的时间合起来
                    int sp = line.LastIndexOf(' ');
                    if (sp <= 0) continue;
                    string toolName = line.Substring(0, sp);
                    string msText = line.Substring(sp + 1).Replace("ms", "").Replace("（失败）", "");
                    if (double.TryParse(msText, out double ms))
                    {
                        toolMs.TryGetValue(toolName, out double old);
                        toolMs[toolName] = old + ms;
                    }
                }
                foreach (string r in t.retryReasons)
                    if (!retryReasons.Contains(r)) retryReasons.Add(r);
            }

            var sb = new StringBuilder();
            sb.Append($"[耗时统计] 第{round}轮 全轮 {wallMs / 1000.0:0.00}s（{all.Count} 个 AI 并行，最长 "
                + (slowest != null ? $"{slowest.who} {slowest.totalMs / 1000.0:0.00}s" : "无") + "）");
            sb.Append(" ｜ 各家：");
            var order = new List<RoundTiming>(all);
            order.Sort((a, b) => b.totalMs.CompareTo(a.totalMs));
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append($"{order[i].who} {order[i].totalMs / 1000.0:0.00}s");
                if (ReferenceEquals(order[i], slowest)) sb.Append("(最慢)");
            }
            // 请求次数要把【格式重试】算进去：那也是 AIRequest 内部又发的一次完整请求
            double sumNormalMs = Math.Max(0.0, sumRequest - sumRetry);
            sb.Append($" ｜ 合计请求 {sumSends + sumFormat} 次 {sumRequest / 1000.0:0.00}s（4 家并行，所以「合计」会大于「全轮」）");
            if (sumOverflow > 0) sb.Append($"(超长重发 {sumOverflow})");
            sb.Append($"（正常 {sumSends} 次 {sumNormalMs / 1000.0:0.00}s");
            if (sumFormat > 0) sb.Append($" + 格式重试 {sumFormat} 次 {sumRetry / 1000.0:0.00}s");
            sb.Append("）");
            if (toolMs.Count > 0)
            {
                var toolText = new List<string>();
                foreach (var kv in toolMs) toolText.Add($"{kv.Key} {kv.Value:0}ms");
                sb.Append($" ｜ 工具（含在请求里）{string.Join("、", toolText)}");
            }
            if (sumBehavior > 0.05) sb.Append($" ｜ 行为检查 {sumBehavior / 1000.0:0.00}s");
            if (sumForcedCount > 0) sb.Append($" ｜ 强制补工具 {sumForcedCount} 次 {sumForced / 1000.0:0.00}s");
            double other = sumTotal - sumRequest - sumBehavior - sumForced;
            sb.Append($" ｜ 其它 {other / 1000.0:0.00}s");

            // 全轮 = 最长那家 + 建情报/收尾这些串行开销，这样两个数字能对上
            double scheduleGap = wallMs - (slowest != null ? slowest.totalMs : 0);
            sb.Append($" ｜ 串行开销 {Math.Max(0.0, scheduleGap) / 1000.0:0.00}s（全轮 − 最长那家）");

            if (slowest != null)
                sb.Append($"\n    最慢样本 {slowest.who}：{slowest.DescribeDetail()}");
            if (retryReasons.Count > 0)
                sb.Append("\n    本轮重试原因：" + string.Join(" / ", retryReasons));

            Debug.Log(sb.ToString());
            AppendTimingCsv(round, wallMs, all, sumSends, sumRequest, sumFormat, sumRetry, sumBehavior);
        }

        /// <summary>
        /// Debug：把这一轮各家的耗时追加到 CSV（`persistentDataPath/Timing/timing.csv`）。
        /// 只用来看"换架构前后"的平均耗时，不参与任何玩法；写失败只打一条 warning。
        /// 列：time,round,who,wallMs,totalMs,requestMs,sends,formatRetries,retryMs,overflowRetries,behaviorMs,toolSumMs,tools
        /// </summary>
        private static void AppendTimingCsv(int round, double wallMs, List<RoundTiming> all,
            int sumSends, double sumRequest, int sumFormat, double sumRetry, double sumBehavior)
        {
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "Timing");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "timing.csv");
                bool isNew = !System.IO.File.Exists(path);

                var sb = new StringBuilder();
                if (isNew)
                    sb.AppendLine("time,round,who,wallMs,totalMs,requestMs,sends,formatRetries,retryMs,overflowRetries,behaviorMs,toolSumMs,tools");

                string stamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                sb.AppendLine($"{stamp},{round},ROUND,{wallMs:0},,,{sumSends},{sumFormat},{sumRetry:0},,{sumBehavior:0},,");

                foreach (RoundTiming t in all)
                {
                    double toolSum = 0;
                    var toolNames = new List<string>();
                    foreach (string line in t.toolLines ?? new List<string>())
                    {
                        int sp = line.LastIndexOf(' ');
                        if (sp <= 0) continue;
                        string nm = line.Substring(0, sp);
                        string msText = line.Substring(sp + 1).Replace("ms", "").Replace("（失败）", "");
                        if (double.TryParse(msText, out double ms)) toolSum += ms;
                        if (!toolNames.Contains(nm)) toolNames.Add(nm);
                    }
                    sb.AppendLine($"{stamp},{round},{t.who},{wallMs:0},{t.totalMs:0},{t.requestMs:0},{t.sends},{t.formatRetries},{t.retryMs:0},{t.overflowRetries},{t.behaviorMs:0},{toolSum:0},{string.Join("|", toolNames)}");
                }

                System.IO.File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[耗时统计] 写 CSV 失败：{e.Message}");
            }
        }

        private System.Diagnostics.Stopwatch roundWatch;
        private RoundTiming currentTiming;
        private long retryClockTick;   // 上一次"格式重试重发"的时刻（0 = 当前不在重试）

        /// <summary>开始统计这一轮（系统时间，不受 timeScale / 录制暂停影响）。</summary>
        private void BeginRoundTiming()
        {
            roundWatch = System.Diagnostics.Stopwatch.StartNew();
            retryClockTick = 0;
            currentTiming = new RoundTiming { round = N, who = name, stage = position };
            // 预行动系统要知道"这是这张卡的第几轮"（reuse 按 AI 轮次算）
            ReadyActionManager.SetRound(position, N);
        }

        /// <summary>这一轮结束：把自己的统计挂到待合并列表，等 4 家都跑完由 LogMergedRoundTiming 合并打一条。</summary>
        private void EndRoundTiming()
        {
            if (currentTiming == null) return;

            roundWatch?.Stop();
            currentTiming.totalMs = roundWatch != null ? roundWatch.Elapsed.TotalMilliseconds : 0;
            CloseRetryClock();
            currentTiming.toolLines = ReactionSystem.TakeToolTimings(position);

            pendingRoundTimings.Add(currentTiming);

            currentTiming = null;
            roundWatch = null;
        }

        /// <summary>格式重试的计时打点：每次"打回重发"前记一笔，下一次（或回合结束）结算这一段。</summary>
        private void MarkRetryStart()
        {
            if (currentTiming == null) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (retryClockTick != 0)
                currentTiming.retryMs += (now - retryClockTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            retryClockTick = now;
        }

        private void CloseRetryClock()
        {
            if (currentTiming == null || retryClockTick == 0) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            currentTiming.retryMs += (now - retryClockTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            retryClockTick = 0;
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
        private const int k = 8;    // 保留最近完整轮数，与 last_round_index 的 8 对应
        private const float b = 30f; // DeepSeek 未命中/命中价格比

        // 连续系统录制回合未调用工具统计；超过 2 个系统回合未用工具时追加提醒
        private int roundsWithoutTool;
        private bool noToolReminderSent;

        // 三段格式回复校验与重试（不合格时无限重试，直到合格）
        /// <summary>兜底用的通用 [格式修正]（说不上具体原因时发它）。正常走 BuildThinkingRetryPrompt。</summary>
        private const string ThinkingRetryPrompt =
            "[格式修正] 你上一条回复不合格。请严格按三段格式重发（内容写在【】里面）：\n" +
            "【分析：……】用（我想：……）写内心独白\n" +
            "【我要：……】这一轮打算做什么\n" +
            "【我说：……】当众说出口的那一句（15 字以内、不许括号、禁止 [skip]；没有就留空）";

        /// <summary>
        /// 逐条列出这次回复**到底哪里不合格**。校验和提醒都用它，保证"提醒里说的"就是"校验拦的"。
        /// </summary>
        private static List<string> ThinkingProblems(DeepSeekMessage msg, bool allowEmptySay = false)
        {
            var reasons = new List<string>();
            string content = msg?.content ?? "";
            bool hasTools = msg != null && msg.tool_calls != null && msg.tool_calls.Count > 0;

            string analysis = Segment(content, "分析");
            string want = Segment(content, "我要");
            string say = Segment(content, "我说");

            // 先卡"写全了没"：缺开头、或缺收尾的 】都要点名
            foreach (string label in SegLabels)
                if (content.IndexOf("【" + label + "：", StringComparison.Ordinal) < 0 && content.IndexOf("【" + label + ":", StringComparison.Ordinal) < 0)
                    reasons.Add("缺少【" + label + "：……】这一段");
                else if (!HasClosedSegment(content, label))
                    reasons.Add("【" + label + "：】没有收尾的「】」（三段都要用【】包完整）");

            if (analysis == null) { /* 上面已经报过"缺这一段" */ }
            else if (string.IsNullOrWhiteSpace(analysis)) reasons.Add("【分析：】是空的：里面要写你的内心独白");
            else if (!analysis.Contains("（我想：")) reasons.Add("【分析：】里没有「（我想：」——内心独白必须写成（我想：……）");

            if (want != null && string.IsNullOrWhiteSpace(want)) reasons.Add("【我要：】是空的：写清这一轮做什么，没有就写「什么都不做」");

            if (say != null)
            {
                if (!allowEmptySay && !hasTools && string.IsNullOrWhiteSpace(say))
                    reasons.Add("【我说：】是空的、又没有调用任何工具：要么写一句当众说的话（15 字以内），要么调工具行动");
                if (ContainsAnyParenthesis(say)) reasons.Add("【我说：】里出现了括号（中英文都不行）");
                if (say.Contains("我想：") || say.Contains("我想:")) reasons.Add("【我说：】里出现了「我想：」——那是【分析】里才写的");
                if (say.Contains("[skip]")) reasons.Add("【我说：】里出现了 [skip]");
            }

            return reasons;
        }

        /// <summary>
        /// 按具体原因拼一条 [格式修正]：直接告诉模型刚才错在哪，别让它自己猜
        /// （它猜错一次就白烧一整轮）。
        /// </summary>
        private static string BuildThinkingRetryPrompt(DeepSeekMessage msg, bool allowEmptySay = false)
        {
            List<string> reasons = ThinkingProblems(msg, allowEmptySay);
            if (reasons.Count == 0) return ThinkingRetryPrompt;   // 兜底

            var sb = new StringBuilder();
            sb.AppendLine("[格式修正]你上一条回复不合格，**具体原因**：");
            foreach (string r in reasons) sb.AppendLine("· " + r);
            sb.AppendLine("请严格按三段格式重发，内容写在【】里面：");
            sb.AppendLine("【分析：……】（用（我想：……）写内心独白）");
            sb.AppendLine("【我要：……】（这一轮打算做什么）");
            sb.AppendLine("【我说：……】（当众说出口的那一句，15 字以内、不许括号；不说话就留空）");
            sb.Append("不要解释这条提醒、也不要猜原因，直接重发。");
            return sb.ToString();
        }

        // ---------- AI 行为检查 ----------
        private const string BehaviorCheckerModel = "deepseek-flash";

        private const string BehaviorCheckerSystemPrompt =
@"你是一个严格的游戏 AI 行为检查器。你的任务只有：判断目标 AI 是否“说了要做某个具体行动，但没有调用对应工具执行”。你不调用任何工具，只输出分析文字和最终判断。

目标 AI 可用的工具只有 6 个：
- use_prop：使用自己武器栏里的道具（护盾、霰弹、扫射、大球、穿甲等）
- merge_prop：把两个同种道具合并成一个（数值相加、省一个槽位，免费）
- ready_action：登记/管理预行动（等条件满足后自动替他执行某个工具；如「槽满就自动把最小的大球打出去」）
- control_turret：控制自己炮塔瞄准
- move_turret：移动自己的炮塔（第一次调用只是预览、不会移动，确认后才会真的移动）
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
            "merge_prop",
            "ready_action",
            "control_turret",
            "move_turret",
            "whisper"
        };

        private bool openingRetryMode;
        private List<DeepSeekMessage> openingLastMessages;

        /// <summary>开局那一轮的赛前宣言与思考，供开场演出按顺序播。</summary>
        public string openingSpeech = "";
        public string openingThinking = "";
        private int n_0_index = 1;//排除系统消息
        private List<int> last_round_index = new();

        // 上下文超长自救：API 报 "maximum context length" 时置位，下一次请求前强制压缩；
        // 压缩还压不下来就按 ContextTrimRatio 一档砍掉最前面的历史（system 与人设不删）。
        private bool contextOverflowPending;
        /// <summary>每次"砍前面"删掉的比例：条数与 token 两项都要压掉这么多。</summary>
        private const float ContextTrimRatio = 0.2f;
        /// <summary>砍前面时至少保留的历史条数（当轮情报就在最后几条里，别砍没了）。</summary>
        private const int ContextTrimKeepTail = 4;

        // 思考校验重试：本轮已插入的 [格式修正]（合格后移除），保证每轮最多一条。
        // 重发次数不设上限：格式不对就一直重发到对为止。
        private DeepSeekMessage roundRetryReminder;
        // 当轮请求是否在飞：用于安全补写升级对话，避免插断 assistant(tool_calls) 与它的 tool 结果
        private bool roundRequestInFlight;
        private readonly List<DeepSeekMessage> deferredUpgradeMessages = new();
        // 升级对话的消息引用集合：不参与公开发言拼接
        private readonly HashSet<DeepSeekMessage> upgradeExchangeMessages = new();

        private int EstimateHistoryTokens(List<DeepSeekMessage> messages)
        {
            int total = 0;
            foreach (DeepSeekMessage m in messages) total += EstimateMessageTokens(m);
            return total;
        }

        /// <summary>单条消息的 token 估算（含 role / 工具调用开销）。</summary>
        private static int EstimateMessageTokens(DeepSeekMessage m)
        {
            if (m == null) return 0;

            int total = AIRequest.TokenEstimator.EstimateTokensCached(m.content);
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
            return total + 4; // role / 结构开销
        }

        /// <summary>这次报错是不是"上下文超长"（API 返回 maximum context length）。</summary>
        private static bool IsContextLengthError(string error)
            => !string.IsNullOrEmpty(error)
               && (error.Contains("maximum context length")
                   || error.Contains("reduce the length of the messages"));

        /// <summary>连续因为超长失败的次数：第 1 次只强制压缩，之后每次都再砍掉一档最前面的历史。</summary>
        private int contextOverflowStrikes;

        /// <summary>
        /// 上下文超长自救：先强制压缩（不管 ShouldCompress 的省钱判据）；
        /// 上一次压缩后重发还是被拒（或估算确实还超），就按 ContextTrimRatio 一档砍掉最前面的历史
        /// —— 所有 system 消息（人设就在第一条 system 里）永远不删。
        /// 注意：砍掉的是模型的长期记忆，被砍的那段它自己也看不到了。
        /// </summary>
        private void RecoverFromContextOverflow()
        {
            Compress();
            n_0 = N;

            int afterCompress = EstimateHistoryTokens(history);
            Debug.LogWarning($"[上下文超限] {name}：已强制压缩（第 {contextOverflowStrikes} 次），估算 {afterCompress} tokens（上限 {AIRequest.MaxTokens}）");

            if (contextOverflowStrikes > 1 || afterCompress > AIRequest.MaxTokens)
            {
                int dropped = TrimOldestHistory(ContextTrimRatio);
                Debug.LogWarning($"[上下文超限] {name}：压缩后仍超，已砍掉最前面的 {dropped} 条历史（system/人设保留），估算 {EstimateHistoryTokens(history)} tokens");
            }
        }

        /// <summary>
        /// 删掉最前面的一批历史：跳过所有 system 消息（人设就在第一条 system 里）。
        /// 条数与估算 token 都要压掉至少 ratio 这么多；最后 ContextTrimKeepTail 条永远留着（当轮情报就在里面）。
        /// 切点往后对齐到"完整消息组"的边界，避免留下没有 assistant 的 tool 结果。返回实际删掉的条数。
        /// </summary>
        private int TrimOldestHistory(float ratio)
        {
            if (history == null || history.Count == 0) return 0;

            int first = 0;
            while (first < history.Count && history[first] != null && history[first].role == "system") first++;
            int limit = Mathf.Max(first, history.Count - ContextTrimKeepTail);
            if (limit <= first) return 0;

            int removable = history.Count - first;
            int minCount = Mathf.Max(1, Mathf.CeilToInt(removable * ratio));
            int needTokens = Mathf.CeilToInt(EstimateHistoryTokens(history) * ratio);

            int end = first;
            int droppedTokens = 0;
            while (end < limit)
            {
                droppedTokens += EstimateMessageTokens(history[end]);
                end++;
                if ((end - first) >= minCount && droppedTokens >= needTokens) break;
            }

            // 开头不能是 tool 结果（它的 assistant 被删掉就成了孤儿消息，接口会 400）
            while (end < history.Count - 1 && history[end] != null && history[end].role == "tool") end++;
            if (end <= first) return 0;

            int drop = end - first;
            // 历史变短了，所有记着下标的账都要跟着往前挪（挪不动的就贴到新的开头）
            for (int i = 0; i < last_round_index.Count; i++)
                last_round_index[i] = Mathf.Max(first, last_round_index[i] - drop);
            n_0_index = Mathf.Max(first, n_0_index - drop);

            history.RemoveRange(first, drop);
            return drop;
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

            // 每次压缩后重置“连续未用工具”提醒状态：每个压缩周期最多提醒一次
            noToolReminderSent = false;
            roundsWithoutTool = 0;
            return true;
        }

        /// <summary>请求直到正确。allowEmptySay = 这一轮允许【我说】为空（开局那句长期规划专用）。</summary>
        public async Task UntilGreatRequest(int roundStartIndex, bool allowEmptySay = false)
        {
            // 上下文超长自救：最多重发这么多次，别让一回合被卡死
            const int maxOverflowRetry = 3;
            int overflowRetry = 0;

            while (true)
            {
                TaskCompletionSource<bool> tcs = new();
                bool overflowed = false;

                RequestInfo info = new(
                    request,
                    msgs =>
                    {
                        if (!_isRunning) return;
                        // 检查 AI 回复 content，出现括号就在 history 末尾追加提醒，下一次请求会带过去
                        RemindIfUsesParentheses(msgs);
                        contextOverflowStrikes = 0;      // 这一发成功了，超长自救的计数清零
                        contextOverflowPending = false;
                        tcs.SetResult(true);
                        },
                    error =>
                    {
                        ReceiveError(error);
                        overflowed = IsContextLengthError(error);
                        tcs.SetResult(true);
                    },
                    toolkit,
                    true,
                    position);

                info.apiKey = LoadApiKey();
                info.apiUrl = url;

                //保证已经检查完毕
                info.validateAndMaybeRetry = (msg) => ValidateRoundReply(msg, info, allowEmptySay);

                roundRequestInFlight = true;
                try
                {
                    // Debug 耗时：这一发请求（含它内部的格式重试、含工具执行）等了多少秒
                    var sendWatch = System.Diagnostics.Stopwatch.StartNew();
                    AIRequest.SendRequest(info);
                    await tcs.Task;
                    sendWatch.Stop();

                    if (currentTiming != null)
                    {
                        currentTiming.sends++;
                        currentTiming.requestMs += sendWatch.Elapsed.TotalMilliseconds;
                        if (overflowed) currentTiming.overflowRetries++;
                    }
                }
                finally
                {
                    roundRequestInFlight = false;
                    FlushDeferredUpgradeMessages();
                    DropMoveShotImages();   // 这一轮的移动截图已经用过了，降级成纯文本，别留到下一轮
                }

                if (!overflowed) break;

                // 超长被拒：先强制压缩（无视 ShouldCompress 的省钱判据）；
                // 估算还压不下来就砍掉最前面的内容（system 与人设不删），然后立刻重发这一轮。
                overflowRetry++;
                contextOverflowPending = false;
                RecoverFromContextOverflow();
                if (overflowRetry >= maxOverflowRetry)
                {
                    Debug.LogError($"[上下文超限] {name}：连续 {overflowRetry} 次自救仍失败，这一轮放弃");
                    break;
                }
            }

            string roundContent = JoinRoundAssistantContents(roundStartIndex);
            if (!string.IsNullOrWhiteSpace(roundContent))
            {
                InformGetter.StageSpeech(position, roundContent);
                RemindIfInvalidEmotions(roundContent);

                // 开局这一轮交给开场演出按顺序播（先思考、再放狠话）；但**同时也要走原来的通道**：
                // 炮塔飘字 + 中央消息列表照样显示这句开场白（不再因为"演出会播"就把旧通道压掉）。
                bool openingHandled = AIAgent.Instance != null && AIAgent.Instance.OpeningRoundActive;
                if (openingHandled)
                {
                    openingSpeech = roundContent;
                    openingThinking = GetLastAssistantReasoning(out _) ?? "";
                }
                if (SpeechPass == 0)
                    Say(position, roundContent);
            }
        }

        /// <summary>三段格式校验：不合格时本轮只插入一条 [格式修正]，最终合格就把这条提醒移除。
        /// allowEmptySay = 这一轮允许【我说】为空（开局那句长期规划就是只想不说，不发言）。</summary>
        private bool ValidateRoundReply(DeepSeekMessage msg, RequestInfo info, bool allowEmptySay = false)
        {
            if (IsThreeSegmentReply(msg, allowEmptySay))
            {
                RemoveRoundRetryReminder();
                return true;
            }

            // 不合格就重发，**不设次数上限**：一定要拿到合规的三段格式才放行。
            if (roundRetryReminder == null)
            {
                roundRetryReminder = new DeepSeekMessage("user", BuildThinkingRetryPrompt(msg, allowEmptySay));
                info.AddMessage(roundRetryReminder);
                Debug.LogWarning($"[AIRequest] 三段格式校验失败，已拦截并重发。原因：{string.Join(" / ", ThinkingProblems(msg, allowEmptySay))}\ncontent: {msg?.content}");
            }
            else
            {
                Debug.LogWarning($"[AIRequest] 三段格式校验再次失败（本轮只插一条提醒，不重复插入）。原因：{string.Join(" / ", ThinkingProblems(msg, allowEmptySay))}\ncontent: {msg?.content}");
            }

            // Debug 耗时：记下"格式重试"的次数、原因，并给重发打一个时间点
            if (currentTiming != null)
            {
                currentTiming.formatRetries++;
                foreach (string reason in ThinkingProblems(msg, allowEmptySay))
                    if (!currentTiming.retryReasons.Contains(reason)) currentTiming.retryReasons.Add(reason);
            }
            MarkRetryStart();

            AIRequest.SendRequest(info);
            return false;
        }

        /// <summary>最终回复合格后，把本轮插入的 [格式修正] 从历史里移除。</summary>
        private void RemoveRoundRetryReminder()
        {
            if (roundRetryReminder == null) return;
            history.Remove(roundRetryReminder);
            roundRetryReminder = null;
        }

        /// <summary>
        /// 把已经用完的移动截图降级成纯文本：清掉 contentBlocks，只留那段说明文字。
        /// 不清的话，同一张图会被之后每一轮请求反复上传（每张最多约 1024 token）。
        /// </summary>
        public void DropMoveShotImages()
        {
            if (history == null || history.Count == 0) return;

            int dropped = 0;
            for (int i = 0; i < history.Count; i++)
            {
                DeepSeekMessage m = history[i];
                if (m == null || m.contentBlocks == null) continue;
                m.contentBlocks = null;
                dropped++;
            }

            if (dropped > 0)
                Debug.Log($"[移动截图] {name} 的 {dropped} 条截图消息已降级为纯文本（不再重复上传）。");
        }

        /// <summary>把一次升级的完整新增对话写回历史（这条对话不参与公开发言拼接）。</summary>
        public void AppendUpgradeExchange(List<DeepSeekMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;

            foreach (DeepSeekMessage m in messages)
                if (m != null) upgradeExchangeMessages.Add(m);

            if (roundRequestInFlight) deferredUpgradeMessages.AddRange(messages);
            else history.AddRange(messages);
        }

        /// <summary>当轮请求落地后，把等待中的升级对话补写进历史。</summary>
        private void FlushDeferredUpgradeMessages()
        {
            if (deferredUpgradeMessages.Count == 0) return;
            history.AddRange(deferredUpgradeMessages);
            deferredUpgradeMessages.Clear();
        }
        private void Say(int position,string content)
        {
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
        public async Task FirstRequest(string inform, string extra)
        {
            N++;
            BeginRoundTiming();   // Debug：开局这一轮也统计（狠话 + 长期规划两句算一轮）

            // 炮塔的开局坐标是在它们 Start 里登记的，可能晚于 Reset()：开局这轮先刷一次 system，
            // 保证「开局各炮塔位置」这一行（以及玩家名单）是最新的。system 不会被压缩，一直留着。
            RefreshSystemPrompt();

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 8) last_round_index.RemoveAt(0);
            int startTokens = EstimateHistoryTokens(history);
            // 开局这轮：情报（存活状态 / 战况）先给，最后一条 user 才是「赛前放狠话」这条指令。
            // 反过来的话模型容易把最后那条情报当指令，直接复述一遍情报当台词（舞台上就是一句【存活状态】…）。
            if (!string.IsNullOrWhiteSpace(extra))
                history.Add(new DeepSeekMessage("user", extra));
            history.Add(new DeepSeekMessage("user", inform));

            int roundStartIndex = history.Count;

            // 有成功缓存就直接复用（开场白 + 开局规划是**一起**缓存的，两句都得在）
            if (TryLoadOpeningCache(out DeepSeekMessage cachedSpeech, out DeepSeekMessage cachedAnalysis))
            {
                history.Add(cachedSpeech);
                history.Add(cachedAnalysis);   // 规划也放回历史，这一局它一直看得见

                // 缓存的这条也必须喂给开场演出：它是从 UntilGreatRequest 的早退路径回来的，
                // 那两个字段本来就只在那里赋值 —— 不补的话舞台上永远是「……」+「没有留下思考过程」，
                // 整场戏等于一个字的 AI 内容都没有。同时照旧走飘字 + 中央消息列表。
                bool openingHandled = AIAgent.Instance != null && AIAgent.Instance.OpeningRoundActive;
                if (openingHandled)
                {
                    openingSpeech = SayOf(cachedSpeech.content);       // 只取【我说】当台词
                    openingThinking = AnalysisOf(cachedSpeech.content); // 【分析】上思考条
                }
                Say(position, SayOf(cachedSpeech.content));

                EndRoundTiming();   // Debug：走缓存的开局也统计一下
                return;
            }

            // ---------- 第一句：赛前狠话 ----------
            request.tool_choice = "none";
            await UntilGreatRequest(roundStartIndex);   // 这一轮的【我说】会被舞台当开场白播
            DeepSeekMessage speechMsg = GetLastAssistantMessage();

            // ---------- 第二句：长期规划（【我说】留空，只写【分析】；这一句把输出上限抬到 4096）----------
            // 思考已经全面关掉，所以"想得深"靠的是给足输出预算：这一句专门给长期规划用 4096。
            int planStartIndex = history.Count;
            history.Add(new DeepSeekMessage("user", OpeningPlanPrompt));
            int normalMaxTokens = request.max_tokens;
            request.max_tokens = OpeningPlanMaxTokens;
            try
            {
                // allowEmptySay：这一句就是"只想不说"（【我说】留空），否则校验会因为没台词打回重写。
                await UntilGreatRequest(planStartIndex, allowEmptySay: true);
            }
            finally
            {
                request.max_tokens = normalMaxTokens;      // 只这一句用 4096
                request.tool_choice = "auto";              // 开局结束后恢复自动工具调用
            }
            DeepSeekMessage planMsg = GetLastAssistantMessage();
            // 第二句请求失败时 GetLastAssistantMessage 还是会返回第一句（狠话）——那种情况下别把它当规划缓存
            if (ReferenceEquals(planMsg, speechMsg)) planMsg = null;

            // 两句一起存成开局缓存：下次同一个 oc 直接复用（见 OpeningCache）
            SaveOpeningCache(speechMsg, planMsg);

            int endTokens = EstimateHistoryTokens(history);
            int sampleX = Mathf.Max(0, endTokens - startTokens);
            avgX = avgX * 0.9f + sampleX * 0.1f;

            Save();
            EndRoundTiming();   // Debug：汇总开局两句的耗时
        }

        public async Task NormalRequest(string inform, string extra)
        {
            N++;
            BeginRoundTiming();   // Debug：回合耗时统计从这里开始

            // 上一轮因为上下文超长被拒、没能当场救回来：这一轮先强制压缩（必要时砍掉最前面的历史）再发
            if (contextOverflowPending)
            {
                contextOverflowPending = false;
                RecoverFromContextOverflow();
            }

            bool compressed = MaybeCompress();

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 8) last_round_index.RemoveAt(0);

            int startTokens = EstimateHistoryTokens(history);
            history.Add(new DeepSeekMessage("user", inform));
            if (!string.IsNullOrWhiteSpace(extra))
                history.Add(new DeepSeekMessage("user", extra));

            // 压缩后不能只改旧历史；要在“这轮新对话”里追加 Tool 使用方法提醒
            if (compressed)
                history.Add(new DeepSeekMessage("user", BuildToolUsageReminder()));

            int roundStartIndex = history.Count;

            //请求直到正确
            await UntilGreatRequest(roundStartIndex);

            int endTokens = EstimateHistoryTokens(history);
            int sampleX = Mathf.Max(0, endTokens - startTokens);
            avgX = avgX * 0.9f + sampleX * 0.1f;

            //言行一致性检查（另一次模型调用，耗时单独记一笔）
            var behaviorWatch = System.Diagnostics.Stopwatch.StartNew();
            List<string> missingTools = await RunBehaviorCheckAsync(roundStartIndex);
            behaviorWatch.Stop();
            if (currentTiming != null)
            {
                currentTiming.behaviorMs = behaviorWatch.Elapsed.TotalMilliseconds;
                currentTiming.behaviorMissing = missingTools != null ? missingTools.Count : 0;
            }
            foreach (string toolName in missingTools)
            {
                // Debug 耗时：被迫补发的工具请求也是完整一轮模型往返，单独记
                var forceWatch = System.Diagnostics.Stopwatch.StartNew();
                await ForceToolCallAsync(toolName);
                forceWatch.Stop();
                if (currentTiming != null)
                {
                    currentTiming.forcedToolCount++;
                    currentTiming.forcedToolMs += forceWatch.Elapsed.TotalMilliseconds;
                }
            }
            //连续未用工具”统计
            UpdateToolUsageReminder(roundStartIndex);

            Save();
            EndRoundTiming();     // Debug：汇总这一轮耗时并打日志
        }


        /// <summary>
        /// 拼接本轮所有 assistant 回复里的【我说】（公开台词）；没有就返回空串。
        /// 三段格式下【分析】是内心独白：【我说】才会被当众播出去、并写进【上一轮发言】给对手读。
        /// </summary>
        private string JoinRoundAssistantContents(int roundStartIndex)
        {
            var parts = new List<string>();
            for (int i = roundStartIndex; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg == null || msg.role != "assistant") continue;
                if (upgradeExchangeMessages.Contains(msg)) continue;   // 升级那次对话不算公开发言

                // 只取【我说】那一段：公开台词。三段格式下【分析】是内心独白，绝不能播出去。
                string said = SayOf(msg.content);
                if (string.IsNullOrWhiteSpace(said)) continue;

                parts.Add(said.Trim());
            }
            return string.Join("\n", parts);
        }

        /// <summary>
        /// 给行为检查用的"本轮说了什么"：【我要】（这一轮打算做什么）+【我说】（当众那句）。
        /// 三段格式下【分析】是内心独白，不喂给检查模型（否则它会把心里的盘算当成"承诺过的行动"）。
        /// </summary>
        private string CollectRoundAssistantContents(int roundStartIndex)
        {
            var parts = new List<string>();
            for (int i = roundStartIndex; i < history.Count; i++)
            {
                DeepSeekMessage msg = history[i];
                if (msg == null || msg.role != "assistant") continue;
                if (upgradeExchangeMessages.Contains(msg)) continue;

                string want = WantOf(msg.content);
                string say = SayOf(msg.content);
                if (!string.IsNullOrWhiteSpace(want)) parts.Add("我要：" + want);
                if (!string.IsNullOrWhiteSpace(say)) parts.Add("我说：" + say);
            }
            return parts.Count == 0 ? "（无）" : string.Join("\n", parts);
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
3. [] 里只能出现 use_prop、control_turret、move_turret、whisper，不得输出其他内容。";
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

        // 行为检查的预筛关键词：只要本轮发言里出现任意一个就交给检查模型。
        // 武器名字跟着 WeaponKind 枚举现算（BuildGameKeywords），以后新增武器不用再手改这里。
        private static readonly List<string> 游戏关键词 = BuildGameKeywords();

        private static List<string> BuildGameKeywords()
        {
            List<string> list = new()
            {
                "球", "盾", "子弹", "弹药", "道具", "武器","穿",
                "炮塔", "瞄准", "移动", "位置", "撞击", "涂", "领土", "弹珠", "升级"
            };
            foreach (WeaponKind kind in MapConfig.AllConcreteWeapons)
            {
                string weaponName = kind.ToString();
                if (!list.Contains(weaponName)) list.Add(weaponName);
            }
            return list;
        }
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
                    $"[行动提醒] 你已经连续 {roundsWithoutTool} 轮没有调用工具。请尽快使用工具采取实际游戏行动，不要只发言或思考。注意：道具不是弹珠，不会越养越大！数值小且没用的道具应该尽快用掉。"));
                Debug.LogWarning($"[AIAgent] {name} 连续 {roundsWithoutTool} 个系统回合未调用工具，已追加行动提醒");
            }
        }
        private const string ParenthesisReminder =
            "[格式提醒] 检测到你上次的回复【我说：】那一段里带了中文或英文括号。下次【我说】里禁止使用任何括号，请严格遵守：\n" +
            "1. 纯文本 + emoji，禁用 markdown，不使用括号，不要动作描写。\n" +
            "2. 别露内心戏，别露你的情报。\n" +
            "3. 夸张化地沉浸在角色中，字数限制在 15 字。\n" +
            "（括号只允许出现在【分析：】那一段里。）";

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
                // 只看【我说】：【分析】里必然有括号（内心独白），那不是违规
                string say = SayOf(message.content);
                if (!ContainsParenthesis(say)) continue;

                // 避免同一个违规反复堆叠提醒
                if (history.Count > 0)
                {
                    DeepSeekMessage last = history[history.Count - 1];
                    if (last.role == "user" && last.content == ParenthesisReminder)
                        return;
                }

                history.Add(new DeepSeekMessage("user", ParenthesisReminder));
                Debug.Log($"[AIAgent] {name} 的【我说】里包含括号，已追加下次禁止括号提醒。原文: {say}");
                return;
            }
        }


        private static readonly HashSet<string> AllowedEmotions = new HashSet<string> { "origin", "smile", "laugh", "shock", "angry", "sad" };
        private static readonly Regex EmoTagScanRegex = new Regex(@"\[\s*emo\s*:\s*([A-Za-z]+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>本级回复里出现白名单外的 [emo:xxx] 时给一次提示（每轮最多一条）。</summary>
        private void RemindIfInvalidEmotions(string roundContent)
        {
            if (string.IsNullOrWhiteSpace(roundContent)) return;

            var bad = new List<string>();
            foreach (Match m in EmoTagScanRegex.Matches(roundContent))
            {
                string value = m.Groups[1].Value.ToLowerInvariant();
                if (!AllowedEmotions.Contains(value) && !bad.Contains(value)) bad.Add(value);
            }
            if (bad.Count == 0) return;

            string reminder = "[格式提醒] 你用了不被支持的表情标记："
                + string.Join("、", bad.Select(v => "[emo:" + v + "]"))
                + "。允许的值只有 origin、smile、laugh、shock、angry、sad，下次请只用这些（非法标记不会生效，会被直接丢掉）。";

            if (history.Count > 0)
            {
                DeepSeekMessage last = history[history.Count - 1];
                if (last != null && last.role == "user" && last.content == reminder) return;
            }

            history.Add(new DeepSeekMessage("user", reminder));
            Debug.Log($"[AIAgent] {name} 使用了非法表情标记：{string.Join("、", bad)}，已追加提示。");
        }

        private void DisplayReceivedMessages(List<DeepSeekMessage> messages)
        {
            foreach (DeepSeekMessage message in messages)
            {
                if (message.role == "assistant")
                {
                    // 只播【我说】：【分析】是内心独白，播出去等于把底牌给全场看
                    string content = SayOf(message.content);
                    if (string.IsNullOrWhiteSpace(content)) continue;
                    if (content.Contains("[skip]")) continue;

                    InformGetter.StageSpeech(position, content);
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

            // 上下文超长这种错：记一笔，请求流程会强制压缩；重发还超就砍掉最前面的历史（system/人设不删）
            if (IsContextLengthError(error))
            {
                contextOverflowPending = true;
                contextOverflowStrikes++;
            }
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

        /// <summary>最近一条 assistant 的【分析】（内心独白）与整段 content。思考字段已经不用了。</summary>
        private string GetLastAssistantReasoning(out string content)
        {
            content = "";
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].role == "assistant")
                {
                    content = history[i].content;
                    return AnalysisOf(history[i].content);
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
