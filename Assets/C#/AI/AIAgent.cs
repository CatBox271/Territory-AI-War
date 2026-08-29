using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using System;

/// <summary>
/// 拆分，在agent里留视频流程控制其他去掉
/// </summary>
public class AIAgent : MonoBehaviour
{
    #region 视频流程
    
    [SerializeField] private float _frame_rate = 60;
    [SerializeField] private float _cycleInterval = 2f;
    [SerializeField] private ReactionSystem reactionSystem;//行动系统：AI 工具在这里

    public static bool _isRunning;
    private bool _isWaiting;
    private int _round;
    private bool _start = false;

    private static string LoadApiKey()
    {
        var keyPath = Path.Combine(Application.persistentDataPath, "Key.txt");
        if (File.Exists(keyPath))
            return File.ReadAllText(keyPath).Trim();

        Debug.LogError($"[AIAgent] Key.txt not found at {keyPath}");
        return "";
    }

    private void Start()
    {
        CapturePause.Capture = GetComponent<RenderHeads.Media.AVProMovieCapture.CaptureBase>() ?? FindObjectOfType<RenderHeads.Media.AVProMovieCapture.CaptureBase>();

        // 工具注入：把 ReactionSystem 的工具表给 TestCard，并把 Itool 处理器一并传入。
        if (reactionSystem == null) reactionSystem = FindObjectOfType<ReactionSystem>();
        if (reactionSystem != null)
        {
            reactionSystem.stage = TestCard.position;
            if (MarbleManager.Instance != null)
                MarbleManager.Instance.RegisterAIStage(TestCard.position);//空槽升级机制跟随这个 AI 阵营
            else
                Debug.LogWarning("[AIAgent] MarbleManager 不存在，空槽升级机制未注册");
            TestCard.request.tools = reactionSystem.tools;
            TestCard.toolkit = reactionSystem;
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

    private async void RunCycleLoop()
    {
        while (_isRunning)
        {
            //每间隔视频的一段_cycleInterval时间暂停
            //如果这里就开始数据收集呢？
            var tcs = new TaskCompletionSource<bool>();
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
            _isWaiting = false;
        }
    }
    private void TestAIAsyncWithRecord(Action complete)
    {
        _ = RunAIAnalysic(complete);
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
        _isRunning = false;
    }

    //以后需要做个角色管理器

    private CharacterCard TestCard = new("T-1", "你是一个战术推演机器", 1, "255|000|000|255", "https://api.deepseek.com/v1/chat/completions", new());//目前先不管颜色，先让AI能够调用工具

    private async Task RunAIAnalysic(Action complete)
    {
        StringBuilder builder = new();
        InformGetter.GetInfo(builder, TestCard.position);
        await TestCard.SendRequest(builder.ToString());
        complete?.Invoke();
    }

    #endregion

    #region 实际执行

    //先做一个角色卡

    public class CharacterCard
    {
        private static string world = @"一、世界观： 1.在数字世界的大陆上，纷争、冲突蔓延着。为了掌握世界，各色各种性格的领袖，将使用不同的攻击、防御方式、结盟或中立、亦或者按兵不动的外交策略，达成击败所有敌人的最终目的并让自己的领地颜色填满大陆。
二、地图和玩家： 1.地图为1024*1024像素即1M的正方形区域，地图的每一角都有一个玩家，共四位，你是其中一位。
三、底层逻辑： 1.同队数值叠加，敌方数值抵消。
四、填色机制： 1.地图的每一个像素代表单位1，当你发射大球或者子弹时大球和子弹的数值会等量的涂抹在地图上，当数值涂抹完道具就消失。 2.大球会吸收子弹，同队子弹会叠加到大球的数值上，敌方子弹会抵消大球的数值。大球数值越大面积越大，质量越大速度越慢。 3.每个玩家都有一个护盾，护盾能物理阻挡敌方子弹和大球，也遵循【三】，护盾不会阻挡自己的大球和子弹。 4.当敌人攻击到你的炮塔时，你立刻毙命。
五、弹珠台： 弹珠台中每队会有等量的弹珠，从8开始。当弹珠经过障碍落入倍乘区后，弹珠的数值会更具落入的区域×2、×4、×8,其中×2面积最大，×8最小，倍乘后弹珠会回到上方初始位置重新滚落。 当弹珠经过中间落到道具选择区后，你就能选择并获得一个根据弹珠数值的道具了。
六、道具选择及使用：
1. 弹珠落到中间的道具选择区后，你会自动获得一个道具；道具数值等于当时弹珠的数值（即 2 的某次方）。
2. 道具会按获得顺序放进你的武器栏，你可以在每轮信息里的“X号阵营道具栈”看到自己当前持有的道具和数值。
3. 道具上限机制：武器栏最多 5 格，开局解锁前 2 格，之后游戏时间 1 分钟、5 分钟、7 分钟各解锁 1 格。你当前可持有的道具数量不能超过已解锁槽位数；一旦超过，多余道具会从最新获得的开始自动溢出并立即生效，不需要你调用 use_prop。
4. 空槽升级机制：你每有一个已解锁但空着的武器格，就会持续积累升级值；升级值积满后会自动给你额外生成一个弹珠。主动用道具腾出空槽能加快升级，所以不要囤积道具到上限。
5. 你可以通过调用工具 use_prop 使用自己武器栏里的道具，参数 index 是要使用的格子序号，从 1 开始（1=第 1 格，2=第 2 格，以此类推）。
6. 使用需要朝目标发射的道具（霰弹、大球、任意）时，请在调用 use_prop 时同时指定朝向：
   - 优先传 target_guid：填场上信息里给的目标 guid，炮口会转向该实体。
   - 如果没有合适 guid，传 aim_x 和 aim_y：填地图世界坐标，炮口会转向该坐标点。
   - 扫射、护盾不需要朝向，可省略这些参数。
7. 使用成功后道具立即消耗并生效，各类型效果如下：
   - 霰弹：朝指定方向散射多颗子弹，子弹总数值等于道具数值。
   - 扫射：把道具数值加进自己的子弹储备。
   - 护盾：把道具数值加进自己的护盾。
   - 大球：朝指定方向发射一个数值等于道具数值的大球。
    - 任意：默认随机触发上面四种效果中的一种；使用【任意】道具时，可以额外传 weapon 参数，从霰弹、扫射、护盾、大球里指定实际要触发的武器。
8. 只有当你确实持有道具时才允许调用 use_prop；index 不能超过当前持有数量，空槽位或没有道具时禁止调用。
9. 一次回复可以在 tool_calls 里调用多个 use_prop，但每个 index 最多调用一次，因为道具使用后槽位会变化。
七、领土面积说明 0号阵营是默认的无主领土,不必在意。

你要做什么：
1.你的一切信息都是滞后10s的，你要用过时的信息，做出超前的决策。
2.你的思考时间充裕，游戏会在你思考的时候暂停，但是你收到的已经是10s前的信息了。
";
        private static string character_mode_prompt = @"【角色沉浸要求】在你的思考过程（<think>标签内）中，请遵守以下规则：
1. 请以角色第一人称进行内心独白，用括号包裹内心活动，例如“（心想：……）”或“(内心OS：……)”
2. 用第一人称描写角色的内心感受，例如“我心想”“我觉得”“我暗自”等
3. 思考内容应沉浸在角色中，通过内心独白分析剧情和规划回复

在你的思考过程外，正式回答content内：
不要出现你的任何思考的心里话！
你的content内容限制为50字以内。
只需纯文本和emoji，
禁止markdown或者富文本
";

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

        public CharacterCard(string name,string oc,int position, string color,string url,List<Tool> tools)
        {
            this.name = name;
            this.oc = oc;
            this.position = position;
            this.color = color;
            this.url = url;
            request.tools = tools;
            Reset();
        }

        public void Reset()
        {
            history.Clear();
            history.Add(new DeepSeekMessage("system", $"{world}\n\n你叫{name}\n{oc}\n\n{character_mode_prompt}"));
            request.messages = history;
        }

        public async Task SendRequest(string inform)
        {
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
                    tcs.TrySetResult(false);
                },
                toolkit);

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
                    var all_towel = Towel.AllTowel;
                    if (all_towel.TryGetValue(position, out Towel towel))
                    {
                        towel.Say(message.content);
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
