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
    private static int openingRetryPauseCount;
    private static void EnterOpeningRetryPause()
    {
        if (openingRetryPauseCount++ == 0)
            CapturePause.Pause();
    }
    private static void ExitOpeningRetryPause()
    {
        if (openingRetryPauseCount > 0 && --openingRetryPauseCount == 0)
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

    /// <summary>把文本里出现的角色名包成对应阵营色的 TMP 富文本。没有名字表或 MapConfig 时原样返回。</summary>
    public static string ColorizeAINames(string text)
    {
        if (string.IsNullOrEmpty(text) || MapConfig.Instance == null || stageNames.Count == 0) return text;

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
    private bool _isWaiting;
    private int _round;
    private bool _start = false;

    //以后需要做个角色管理器

    private const string ApiUrl = "https://api.deepseek.com/v1/chat/completions";
    private readonly List<CharacterCard> cards = new();
    private readonly HashSet<int> deadStages = new();
    private int soloSinceRound = -1;

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
    }

    private void Start()
    {
        CapturePause.Capture = GetComponent<RenderHeads.Media.AVProMovieCapture.CaptureBase>() ?? FindObjectOfType<RenderHeads.Media.AVProMovieCapture.CaptureBase>();

        // 工具注入：把 ReactionSystem 的工具表注入 4 张角色卡，并把 Itool 处理器一并传入。
        if (reactionSystem == null) reactionSystem = FindObjectOfType<ReactionSystem>();
        if (reactionSystem != null)
        {
            reactionSystem.stage = 1;//兼容旧的单阵营入口，实际以 RequestInfo.toolStage 为准
           cards.Add(new CharacterCard("赤喵",
    "人设：15岁的中二雌小鬼小猫，自称“猩红利爪”。性格急性子、爱嘲讽、得意时“嘻嘻～”笑。劣势时会发出“呜喵？！”等奇怪动静，死不认输。战术风格：开局rush，多线骚扰，越劣势越疯。", 
    1, "255|000|000|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("苍感",
    "人设：20岁的天才战术师，外表冷静正经但偶尔会冒出低烈度粗口。过度思考，容易走神，常说“啊……你刚刚说了什么？”战术风格：侦察优先，防守反击，精于计算。", 
    2, "000|000|255|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("藤延", 
    "人设：绿发阴湿系青年。性格冷漠寡言，但对队友莫名负责，总在暗处默默守护。战术风格：游走消耗，耐心围杀，像鬼一样神出鬼没。", 
    3, "000|255|000|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("耶罗",
    "人设：24岁的疯癫战术家，直觉惊人。性格疯疯癫癫，爱说无厘头胡话。战术风格：不可预测，声东击西，制造混乱。", 
    4, "255|255|000|255", ApiUrl, reactionSystem.tools));

            foreach (CharacterCard card in cards)
            {
                card.toolkit = reactionSystem;
                if (MarbleManager.Instance != null)
                    MarbleManager.Instance.RegisterAIStage(card.position);//空槽升级机制跟随这个 AI 阵营
                else
                    Debug.LogWarning("[AIAgent] MarbleManager 不存在，空槽升级机制未注册");
            }

            stageNames.Clear();
            foreach (CharacterCard card in cards)
                stageNames[card.position] = card.name;

            CharacterCard.SetKnownPlayers(cards);
            foreach (CharacterCard card in cards)
                card.RefreshSystemPrompt();

            WhisperManager.ReplyProvider = WhisperReplyAsync;
        }
        else
        {
            Debug.LogError("[AIAgent] 场景中找不到 ReactionSystem，AI 将无法调用工具");
        }
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
                RunCycleLoop();
            }
        }
    }
    public void StopCycle()
    {
        _isRunning = false;
    }

    private bool cycle_start = true;
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

        if (tasks.Count > 0) await Task.WhenAll(tasks);
        complete?.Invoke();
    }

    private async Task RunCardAsync(CharacterCard card, string inform)
    {
        WhisperManager.SetBusy(card.position, true);
        try
        {
            await card.SendRequest(inform);
        }
        finally
        {
            WhisperManager.SetBusy(card.position, false);
        }
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
        string deathLine = card != null ? $"{card.name}被{killerStage}号阵营击杀" : $"{stage}号阵营被{killerStage}号阵营击杀";
        if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null)
            towel.Say(deathLine, true);

        UpdateSoloState();

        if (card != null) return RequestLastWordsAsync(card, stage, killerStage);
        return Task.FromResult("无言的告别");
    }

    private void UpdateSoloState()
    {
        int alive = 0;
        foreach (CharacterCard card in cards)
            if (!deadStages.Contains(card.position)) alive++;

        if (alive == 1)
        {
            if (soloSinceRound < 0) soloSinceRound = _round;
        }
        else
        {
            soloSinceRound = -1;
        }
    }

    private bool ShouldStopSoloSpeech()
    {
        int alive = 0;
        foreach (CharacterCard card in cards)
            if (!deadStages.Contains(card.position)) alive++;
        return alive == 1 && soloSinceRound >= 0 && _round - soloSinceRound >= 3;
    }

    private async Task<string> RequestLastWordsAsync(CharacterCard card, int stage, int killerStage)
    {
        string words = "无言的告别";
        CapturePause.Pause();
        try
        {
            DeepSeekRequest copy = card.request.DeepCopy();
            if (copy.messages == null) copy.messages = new List<DeepSeekMessage>();
            copy.messages.Add(new DeepSeekMessage("user", $"你刚刚被{killerStage}号阵营击杀。请留下一句遗言，50字以内，纯文本，不要调用工具。"));
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
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(25000));
            if (completed != tcs.Task)
            {
                Debug.LogWarning($"[遗言] stage {stage} 请求超时，使用兜底遗言");
                words = "无言的告别";
            }
            else
            {
                words = await tcs.Task;
                if (string.IsNullOrWhiteSpace(words)) words = "无言的告别";
            }
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
            if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null)
                towel.Say(words, true);
        }
        catch (Exception e)
        {
            Debug.LogError($"[遗言] Say 失败：{e}");
        }
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
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(25000));
        if (completed != tcs.Task) return "（悄悄话回复超时）";
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

    //先做一个角色卡

    public class CharacterCard
    {
        private static string world = @$"# 实时战略游戏 AI 提示词

## 角色与目标
你是一名实时战略游戏 AI。最终目标：**击败其他所有玩家，并让自己的阵营最终控制大陆。**
所有行动必须通过实际调用游戏工具执行，不得仅用文字描述。

## 一、世界与地图
- 地图为 1024×1024 正方形，每个像素代表 1 单位领土。
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
- 每队初始拥有 {MarbleManager.Instance.initialMarbleCount} 个弹珠。
- 弹珠经过障碍后进入倍乘区：×2（面积最大）→ ×4 → ×8（面积最小）；倍乘完成后回到顶部重新滚落。
- 进入道具选择区时随机落到道具上；道具数值等于弹珠当时数值，按 2 的幂次增长。

## 四、武器栏
- 最多 5 格：开局解锁 2 格，1 分钟第 3 格，4 分钟第 4 格，10 分钟第 5 格。
- 道具按获得顺序进入武器栏；超过当前可持有数量时，从最新获得的道具开始溢出并立即生效。

## 五、空槽升级
每个**已解锁且为空**的武器格都会持续积累升级值，达标后**额外生成一个弹珠**；每生成一个，下一次升级所需值翻倍。
因此：使用道具腾出空槽可加快长期资源增长；后期升级耗时变长、槽位解锁多时也应留些底牌。但不要无意义囤积道具。

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

## 行动与发言约束
- 沉默指 content 极简克制（可用。。。或嗯。表达），游戏行动仍必须照常调用工具。
";
        private static string character_mode_prompt = @"【角色沉浸要求】在你的思考过程（<think>标签内）中，请遵守以下规则：
1. 请以角色第一人称进行内心独白，用括号包裹内心活动，例如“（心想：……）”或“(内心OS：……)”
2. 用第一人称描写角色的内心感受，例如“我心想”“我觉得”“我暗自”等
3. 思考内容应沉浸在角色中，通过内心独白分析剧情和规划回复
4. 正式回答只放content内，别露内心戏,别露你的情报，别暴露你的悄悄话，content的内容全局玩家共享！纯文本+emoji，人格化表达，禁用markdown，字数左右25字。
";

        public static string ModePrompt => character_mode_prompt;
        public string name = "";
        public string oc = "";
        public string url = "";
        public int position = -1;//地图上的位置//派系记得告诉AI
        public string color;//派系颜色,000~255 RGBA中间|分割，完整的为如122|122|122|255，
        public DeepSeekRequest request = new() { reasoning_effort = "low"

        };
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

        private void MaybeCompress()
        {
            if (!ShouldCompress()) return;

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
        }

        public async Task SendRequest(string inform)
        {
            N++;
            MaybeCompress();

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 5) last_round_index.RemoveAt(0);

            int startTokens = EstimateHistoryTokens(history);
            history.Add(new DeepSeekMessage("user", inform));
            int roundStartIndex = history.Count;
            bool isOpening = N == 1; // 只在开局放狠话这一轮检测 reasoning，不合格就无限重刷
            bool pausedForRetry = false;
            int retryCount = 0;
            bool openingReused = false;

            // 有成功缓存就直接复用，不再请求/重刷
            if (isOpening && TryLoadOpeningCache(out DeepSeekMessage cachedOpening))
            {
                openingReused = true;
                history.Add(cachedOpening);
                InformGetter.SetAIContent(position, cachedOpening.content);
                if (Towel.AllTowel.TryGetValue(position, out Towel towel))
                    towel.Say(cachedOpening.content, true);
            }

            // 开局特殊处理：从第一次请求开始就暂停录制，直到开场 reasoning 合格
            if (isOpening && !openingReused)
            {
                EnterOpeningRetryPause();
                pausedForRetry = true;
            }

            while (!openingReused)
            {
                if (isOpening)
                {
                    openingRetryMode = true;
                    openingLastMessages = null;
                    request.tool_choice = "none"; // 开局只放狠话，不允许调用工具
                }

                bool timedOut = false;
                //结果等待器
                var tcs = new TaskCompletionSource<bool>();

                RequestInfo info = new(
                    request,
                    msgs =>
                    {
                        if (timedOut) return;
                        ReceiveResponse(msgs);   // 在这里解析
                        tcs.TrySetResult(true);
                    },
                    error =>
                    {
                        if (timedOut) return;
                        ReceiveError(error);
                        tcs.TrySetResult(true);
                    },
                    toolkit,
                    true,
                    position);

                info.apiKey = AIAgent.LoadApiKey();
                info.apiUrl = url;

                try
                {
                    AIRequest.SendRequest(info);
                }
                catch (Exception ex)
                {
                    ReceiveError(ex.Message);
                    tcs.TrySetResult(false);
                }

                await tcs.Task;
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(25000));
                if (completed != tcs.Task)
                {
                    timedOut = true;
                    ReceiveError("AI请求超时(25秒)");
                }

                // 只在开头检测 reasoning_content，不合格就重发
                string reasoning = null;
                bool goodEnough = true;
                if (isOpening)
                {
                    reasoning = GetLastAssistantReasoning();
                    goodEnough = AIRequest.IsRoleplayReasoning(reasoning);
                }

                if (goodEnough)
                    break;

                retryCount++;
                string shortReasoning = reasoning;
                if (shortReasoning != null && shortReasoning.Length > 120)
                    shortReasoning = shortReasoning.Substring(0, 120) + "...";
                Debug.Log($"[开局重刷] {name} reasoning 不合格，第 {retryCount} 次重置重发。reasoning: {shortReasoning}");

                // 清掉本次请求产生的 assistant/tool 消息，保留 user 消息重发
                if (history.Count > roundStartIndex)
                    history.RemoveRange(roundStartIndex, history.Count - roundStartIndex);
            }

            if (isOpening && !openingReused)
            {
                openingRetryMode = false;

                // 直接用最终留在 history 里的 assistant content，确保一定 Say
                string finalContent = GetLastAssistantContent();
                if (!string.IsNullOrWhiteSpace(finalContent))
                {
                    InformGetter.SetAIContent(position, finalContent);
                    if (Towel.AllTowel.TryGetValue(position, out Towel towel))
                        towel.Say(finalContent, true); // 强制顶掉 Start 里的 HelloWorld 等旧气泡
                        DeepSeekMessage finalMsg = GetLastAssistantMessage();
                        if (finalMsg != null)
                            SaveOpeningCache(finalMsg);
                }
                else
                {
                    Debug.LogWarning($"[开局] {name} 最终没有可取 content");
                }
            }

            if (isOpening && !openingReused)
                request.tool_choice = "auto"; // 开局结束后恢复自动工具调用

            if (pausedForRetry)
                ExitOpeningRetryPause();

            int endTokens = EstimateHistoryTokens(history);
            int sampleX = Mathf.Max(0, endTokens - startTokens);
            avgX = avgX * 0.9f + sampleX * 0.1f;

            Save();
        }
        private void ReceiveResponse(List<DeepSeekMessage> messages)
        {
            if (!_isRunning) return;

            // 开头重试期间先不公开展示，等确定保留哪一次再显示
            if (openingRetryMode)
            {
                openingLastMessages = messages;
                return;
            }

            DisplayReceivedMessages(messages);
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

        private string GetLastAssistantContent()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].role == "assistant" && !string.IsNullOrWhiteSpace(history[i].content))
                    return history[i].content;
            }
            return null;
        }

        private string GetLastAssistantReasoning()
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].role == "assistant")
                    return history[i].reasoning_content;
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
