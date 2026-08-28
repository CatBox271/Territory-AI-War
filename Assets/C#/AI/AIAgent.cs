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
    [SerializeField] private float _cycleInterval = 2f;
    private AIRequest _aiRequest;
    [SerializeField] private MessageDisplayer _messageDisplayer;

    private const string API_URL = "https://api.deepseek.com/chat/completions";
    private const string MODEL = "deepseek-v4-flash";

    private bool _isRunning;
    private bool _isWaiting;
    private MonoCharacter[] _characters;
    private int _round;
    private bool _start = false;
    private string _apiKey = "";
    private string _lastReply = "";

    #region 初始化

    //工具
    private static readonly Tool[] TOOLS = new[]
    {
        new Tool
        {
            type = "function",
            function = new Function
            {
                name = "shout_to_other",
                description = "向另一个AI喊话, 用于挑衅、结盟、威胁等。",
                parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["target"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "目标AI编号(1-4)，不能是自己" },
                        ["message"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "喊话内容，最多20字" }
                    },
                    ["required"] = new[] { "target", "message" }
                }
            }
        }
    };
    //初始设定
    private static readonly string[] PERSONALITIES =
    {
        "你是1号AI，性格好斗激进，喜欢挑衅其他AI。你说话简短有力，经常用感叹号。",
        "你是2号AI，性格谨慎保守，说话总是犹豫不决。你喜欢分析利弊",
        "你是3号AI，性格圆滑世故，喜欢结盟和谈条件。你说话礼貌但暗藏心机。",
        "你是4号AI，性格混乱不可预测，经常说莫名其妙的话。你喜欢打断别人，说话跳跃。",
    };
    //初始名字
    private static readonly string[] NAMES = { "1号", "2号", "3号", "4号" };

    void SetDefaultSetting()
    {
        _characters = new MonoCharacter[4];
        for (int i = 0; i < 4; i++)
        {
            _characters[i] = new();
            _characters[i].Creat(NAMES[i], PERSONALITIES[i]);
        }
    }

    private string LoadApiKey()
    {
        var keyPath = Path.Combine(Application.persistentDataPath, "Key.txt");
        if (File.Exists(keyPath))
        {
            var key = File.ReadAllText(keyPath).Trim();
            print($"[AIAgent] API key loaded from {keyPath}");
            return key;
        }

        Debug.LogError($"[AIAgent] Key.txt not found at {keyPath}");
        return "";
    }

    private void Awake()
    {
        _apiKey = LoadApiKey();
        if (_aiRequest == null)
            _aiRequest = GetComponent<AIRequest>() ?? FindObjectOfType<AIRequest>();
        SetDefaultSetting();
    }

    #endregion

    #region 循环控制
    private void Start()
    {
        CapturePause.Capture = GetComponent<RenderHeads.Media.AVProMovieCapture.CaptureBase>() ?? FindObjectOfType<RenderHeads.Media.AVProMovieCapture.CaptureBase>();
        _aiRequest = AIRequest.Instance;
        if (_messageDisplayer == null)
            _messageDisplayer = FindObjectOfType<MessageDisplayer>();
    }

    private void Update()
    {

        if (CapturePause.IsCapturing)
        {
            if (!_start)
            {
                _start = true;
                StartCycle();
            }
        }
    }

    private void OnDestroy()
    {
        _isRunning = false;
    }
    public void StartCycle()
    {
        if (_isRunning) return;
        if (string.IsNullOrEmpty(_apiKey)) return;
        _isRunning = true;
        _round = 0;
        RunCycleLoop();
    }
    public void StopCycle()
    {
        _isRunning = false;
    }

    #endregion

    private async void RunCycleLoop()
    {
        while (_isRunning)
        {
            print($"[AIAgent] Round {_round + 1}  rendering for {_cycleInterval}s");
            await Task.Delay((int)(_cycleInterval * 1000));//实际判断延迟
            if (!_isRunning) break;

            if (_isWaiting)
            {
                print("[AIAgent] Previous round still waiting, skipping");
                continue;
            }

            _isWaiting = true;
            _round++;

            CapturePause.Pause();
            //实际执行

            for (int i = 0; i < _characters.Length; i++)
            {
                if (!_isRunning) break;

                var character = _characters[i];
                var situation = new StringBuilder();
                InformGeter.GetInfo(situation, i + 1);
                character.AddContent("当前战局：\n" + situation, "user");

                bool ok = await RequestAIAsync(character);
                if (!ok) Debug.LogWarning($"[AIAgent] {character.ai_name} 请求失败");
            }

            CapturePause.Resume();
            _isWaiting = false;
        }
    }

    private void OnGUI()
    {
        if (string.IsNullOrEmpty(_lastReply)) return;

        GUI.color = Color.white;
        GUI.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
        GUI.Box(new Rect(10f, 10f, 700f, 60f), _lastReply);
    }
    private void OnAIResponse(MonoCharacter character, List<DeepSeekMessage> msgs)
    {
        if (msgs == null || msgs.Count == 0) return;

        foreach (var msg in msgs)
        {
            if (msg == null) continue;

            if (!string.IsNullOrEmpty(msg.reasoning_content))
                print($"[AIAgent] {character.ai_name} 思考: {msg.reasoning_content}");

            if (!string.IsNullOrEmpty(msg.content))
            {
                print($"[AIAgent] {character.ai_name} 回复: {msg.content}");
                _lastReply = $"{character.ai_name}: {msg.content}";
                if (_messageDisplayer != null)
                    _messageDisplayer.Say(msg.content);
            }
        }
    }
    private Task<bool> RequestAIAsync(MonoCharacter character)
    {
        var tcs = new TaskCompletionSource<bool>();

        var request = new DeepSeekRequest
        {
            model = MODEL,
            stream = false,
            thinking = new ThinkingConfig(true),
            reasoning_effort = "high",
            messages = character.messages,
            tools = new List<Tool>(TOOLS)
        };

        var requestInfo = new RequestInfo(
            request,
            msgs =>
            {
                OnAIResponse(character, msgs);
                tcs.TrySetResult(true);
            },
            error =>
            {
                Debug.LogError($"[AIAgent] {character.ai_name} 请求错误: {error}");
                tcs.TrySetResult(false);
            },
            new AIAgentToolkit(this),
            true)
        {
            apiUrl = API_URL,
            apiKey = _apiKey
        };

        _aiRequest.SendRequest(requestInfo);
        return tcs.Task;
    }

    private class AIAgentToolkit : Itool
    {
        private readonly AIAgent _owner;

        public AIAgentToolkit(AIAgent owner)
        {
            _owner = owner;
        }

        public IEnumerator DealToolCallsCoroutine(List<ToolCall> toolCalls, Action<List<DeepSeekMessage>> onComplete)
        {
            var results = new List<DeepSeekMessage>();
            foreach (var tc in toolCalls)
            {
                string resultText;
                try
                {
                    var args = JsonConvert.DeserializeObject<Dictionary<string, object>>(tc.function.arguments ?? "{}") ?? new Dictionary<string, object>();
                    switch (tc.function.name)
                    {
                        case "shout_to_other":
                            int target = args.TryGetValue("target", out var targetObj) ? System.Convert.ToInt32(targetObj) : 0;
                            string message = args.TryGetValue("message", out var msgObj) ? System.Convert.ToString(msgObj) : "";
                            _owner.Shout(target, message);
                            resultText = "ok";
                            break;
                        default:
                            resultText = "unknown tool: " + tc.function.name;
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    resultText = "error: " + ex.Message;
                }

                results.Add(new DeepSeekMessage("tool", resultText) { tool_call_id = tc.id });
            }

            onComplete?.Invoke(results);
            yield break;
        }
    }

    private void Shout(int target, string message)
    {
        if (target < 1 || target > _characters.Length)
        {
            Debug.LogWarning($"[AIAgent] 无效目标 {target}");
            return;
        }

        _characters[target - 1].AddContent(message);
    }


    /// <summary>将喊话内容注入其他人的 history</summary>
    private void Broadcast(string content)
    {
        foreach (var mono in _characters)
        {
            mono.AddContent(content);
        }
    }



}