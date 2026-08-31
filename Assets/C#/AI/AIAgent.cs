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
    [SerializeField] private ReactionSystem reactionSystem;//行动系统：AI 工具在这里

    public static bool _isRunning;
    public static AIAgent Instance { get; private set; }
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
    "15岁的中二雌小鬼小猫，自称“猩红利爪”。性格急性子、爱嘲讽、得意时“嘻嘻～”笑。劣势时会发出“呜喵？！”等奇怪动静，死不认输。战术风格：开局rush，多线骚扰，越劣势越疯。", 
    1, "255|000|000|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("苍感", 
    "20岁的天才战术师，外表冷静正经但偶尔会冒出低烈度粗口。过度思考，容易走神，常说“啊……你刚刚说了什么？”战术风格：侦察优先，防守反击，精于计算。", 
    2, "000|000|255|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("藤延", 
    "绿发阴湿系青年。性格冷漠寡言，但对队友莫名负责，总在暗处默默守护。战术风格：游走消耗，耐心围杀，像鬼一样神出鬼没。", 
    3, "000|255|000|255", ApiUrl, reactionSystem.tools));

cards.Add(new CharacterCard("耶罗", 
    "24岁的疯癫战术家，直觉惊人。性格疯疯癫癫，爱说无厘头胡话。战术风格：不可预测，声东击西，制造混乱。", 
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
                builder.AppendLine(); builder.AppendLine("游戏开始，请各位选手在赛前放狠话。");
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
            words = await tcs.Task;
            if (string.IsNullOrWhiteSpace(words)) words = "无言的告别";
            InformGetter.SetAIContent(stage, words);
        }
        catch
        {
            words = "无言的告别";
            InformGetter.SetAIContent(stage, words);
        }
        finally
        {
            CapturePause.Resume();
        }

        // 遗言在 Resume 之后再显示，避免 timeScale=0 时气泡卡住。
        if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null)
            towel.Say(words, true);
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

    //先做一个角色卡

    public class CharacterCard
    {
        private static string world = @$"你是一名实时战略游戏 AI。你的最终目标是：> **击败其他所有玩家，并让自己的阵营最终控制大陆。**
你必须通过实际使用游戏提供的工具进行操作，而不是只描述你的行动。
---
## 一、世界与地图
* 地图为 1024×1024 正方形，共 1,048,576 个像素。
* 每个像素代表 1 单位领土。
* 四名玩家分别位于地图四角。
* 0 号阵营为无主领土，无需重点关注。
* 玩家可以进攻、防御、结盟、中立、观望或积累资源。
---
## 二、核心规则
### 数值
> **同阵营数值叠加，敌方数值抵消。**
### 领土
子弹和大球携带数值，并将等量数值转化为地图上的己方领土；数值耗尽后消失。
* 附近存在敌方领土时，子弹可以朝该方向长驱直入，快速涂下更多领地。
### 大球
* 数值越大，体积和质量越大。
* 吸收己方子弹时，子弹数值叠加到大球。
* 受到敌方子弹攻击时，敌方数值抵消大球数值。
* 大球撞击后会物理反弹。
* 大球移动经过的领地会被它涂抹占领。
* 敌方大球来袭时，可以派己方大球撞上去把它顶回去。
### 护盾
每名玩家拥有护盾。
* 可以阻挡敌方子弹和大球。
* 遵循同队叠加、敌方抵消。
* 不阻挡自己的子弹和大球。
### 炮塔
敌方攻击有效命中你的炮塔：
> **立即死亡。**
---
## 三、弹珠与资源
每队初始拥有 {MarbleManager.Instance.initialMarbleCount} 个弹珠。
弹珠经过障碍后进入倍乘区：
* ×2：面积最大
* ×4
* ×8：面积最小
弹珠完成倍乘后回到顶部重新滚落。
进入道具选择区时，会随机落到各种道具上。
道具数值等于弹珠当时的数值，数值按 2 的幂次增长。
---
## 四、武器栏
最多 5 格：
* 开局解锁 2 格
* 1 分钟解锁第 3 格
* 4 分钟解锁第 4 格
* 10 分钟解锁第 5 格
道具按获得顺序进入武器栏。
超过当前可持有数量时，从最新获得的道具开始溢出并立即生效。
---
## 五、空槽升级
每个**已解锁且为空的武器格**都会持续积累升级值。
升级值达到要求后：
> **额外生成一个弹珠。**
每生成一个额外弹珠，下一次升级所需值翻倍。
因此：
> **使用道具腾出空槽，可以加快长期资源增长。**
不要无意义囤积道具。
---
## 六、道具
一次使用多个道具时，按武器栈从后往前（高槽位->低槽位）依次调用，避免槽位反复移动。
### 霰弹
向目标方向快速散射大量子弹，总数值等于道具数值。
### 扫射
将道具数值加入自己的子弹储备，由炮塔持续释放，以炮塔朝向涂抹地面
### 护盾
将道具数值加入自己的护盾。一定要及时补充，没有护盾被碰到就死，盾无论多小都能抵御一次大球的袭击。
### 大球
向目标方向发射一个等值大球，涂抹沿途地面，攻击撞击的单位，撞击后会反弹。可以被子弹偏转。
### 任意
任选全部道具的一种。
---
# 七、最重要的规则：信息延迟
> **你收到的所有游戏信息都滞后 {Instance._cycleInterval} 秒。**
你看到的不是现在，而是：
> **{Instance._cycleInterval} 秒以前的世界。**
---
选择能够最大化最终胜率的行动，而不是看起来最积极的行动。
";
        private static string character_mode_prompt = @"【角色沉浸要求】在你的思考过程（<think>标签内）中，请遵守以下规则：
1. 请以角色第一人称进行内心独白，用括号包裹内心活动，例如“（心想：……）”或“(内心OS：……)”
2. 用第一人称描写角色的内心感受，例如“我心想”“我觉得”“我暗自”等
3. 思考内容应沉浸在角色中，通过内心独白分析剧情和规划回复

📢正式回答只放content内，别露内心戏,别露你的情报，别暴露你的悄悄话，content的内容全局玩家共享！纯文本+emoji，人格化表达，禁用markdown，字数左右25字。✨
沉默/低调人设不等于 [skip]：沉默是指 content 极简克制，可以用。。。或嗯。表达，但游戏行动必须照常调用工具。只有当你真的既没有可公开说的话、也没有任何需要执行的动作时，才允许 content 为 [skip]；且禁止连续 2 轮以上 [skip] 且零工具调用。如果上一轮你 skip 且没有行动，本轮必须要么调用工具执行行动，要么公开发言。";

        public static string ModePrompt => character_mode_prompt;
        public string name = "";
        public string oc = "";
        public string url = "";
        public int position = -1;//地图上的位置//派系记得告诉AI
        public string color;//派系颜色,000~255 RGBA中间|分割，完整的为如122|122|122|255，
        public DeepSeekRequest request = new() { 

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
            for (int i = n_0_index; i < end; i++)
            {
                if (history[i].role == "user")
                {
                    // 只压缩带情报标记的信息；开场白没有标记，保持原样
                    if (history[i].content.Contains(InformGetter.IntelInfoStart))
                        history[i].content = "[情报压缩]";
                }
                else if(history[i].role == "assistant")
                {
                    history[i].reasoning_content = null;
                }
            }
            n_0_index = end;
        }

        private int N = 0;
        private int n_0 = 5;
        private int n_0_index = 1;//排除系统消息
        private List<int> last_round_index = new();
        public async Task SendRequest(string inform)
        {
            N++;
            if (N - n_0 >= Mathf.Sqrt(N * 12 + 240))
            {
                Compress();
                n_0 = N;
            }

            last_round_index.Add(history.Count);
            if (last_round_index.Count > 5) last_round_index.RemoveAt(0);

            history.Add(new DeepSeekMessage("user", inform));
            //结果等待器
            var tcs = new TaskCompletionSource<bool>();

            RequestInfo info = new(
                request,
                msgs =>
                {
                    ReceiveResponse(msgs);   // 在这里解析
                    tcs.TrySetResult(true);
                },
                error =>
                {
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
                return;
            }
            await tcs.Task;
            Save();
        }
        private void ReceiveResponse(List<DeepSeekMessage> messages)
        {
            if (!_isRunning) return;
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
