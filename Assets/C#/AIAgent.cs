using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;
using RenderHeads.Media.AVProMovieCapture;

public class AIAgent : MonoBehaviour
{
    [SerializeField] private CaptureBase _capture;
    [SerializeField] private float _cycleInterval = 2f;
    [SerializeField] private bool _autoStart = true;

    private string _apiKey;
    private bool _isRunning;
    private bool _isWaiting;
    private List<ChatMessage>[] _histories;
    private int _round;

    private const string API_URL = "https://api.deepseek.com/chat/completions";
    private const string MODEL = "deepseek-chat";

    private static readonly JsonSerializerSettings JSON_SETTINGS = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
    };

    private static readonly string[] PERSONALITIES =
    {
        "你是1号AI，性格好斗激进，喜欢挑衅其他AI。你说话简短有力，经常用感叹号。你讨厌4号。",
        "你是2号AI，性格谨慎保守，说话总是犹豫不决。你喜欢分析利弊，经常用\"但是\"。你害怕1号。",
        "你是3号AI，性格圆滑世故，喜欢结盟和谈条件。你说话礼貌但暗藏心机。你想拉拢2号。",
        "你是4号AI，性格混乱不可预测，经常说莫名其妙的话。你喜欢打断别人，说话跳跃。你无视1号的挑衅。",
    };

    private static readonly string[] NAMES = { "1号", "2号", "3号", "4号" };

    private void Awake()
    {
        var keyPath = Path.Combine(Application.persistentDataPath, "Key.txt");
        if (File.Exists(keyPath))
        {
            _apiKey = File.ReadAllText(keyPath).Trim();
            print("[AIAgent] API key loaded");
        }
        else
        {
            Debug.LogError($"[AIAgent] Key.txt not found at {keyPath}");
        }

        _histories = new List<ChatMessage>[4];
        for (int i = 0; i < 4; i++)
            _histories[i] = new List<ChatMessage>();
    }

    private void Start()
    {
        if (_autoStart)
            StartCycle();
    }

    private void OnDestroy()
    {
        _isRunning = false;
    }

    [ContextMenu("Start AI Cycle")]
    public void StartCycle()
    {
        if (_isRunning) return;
        _isRunning = true;
        _round = 0;
        RunCycleLoop();
    }

    [ContextMenu("Stop AI Cycle")]
    public void StopCycle()
    {
        _isRunning = false;
    }

    private async void RunCycleLoop()
    {
        while (_isRunning)
        {
            print($"[AIAgent] Round {_round + 1} — rendering for {_cycleInterval}s");
            await Task.Delay((int)(_cycleInterval * 1000));

            if (!_isRunning) break;

            if (_isWaiting)
            {
                print("[AIAgent] Previous round still waiting, skipping");
                continue;
            }

            _isWaiting = true;
            _round++;

            PauseCapture();

            var tasks = new Task<string>[4];
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                tasks[i] = SendAIRequest(idx);
            }

            var responses = await Task.WhenAll(tasks);

            if (!_isRunning) { _isWaiting = false; return; }

            for (int i = 0; i < 4; i++)
            {
                if (string.IsNullOrEmpty(responses[i])) continue;

                var msg = ParseResponseMessage(responses[i]);
                if (msg == null) continue;

                if (msg.ToolCalls != null && msg.ToolCalls.Length > 0)
                {
                    foreach (var tc in msg.ToolCalls)
                        ExecuteToolCall(i, tc);
                }

                if (!string.IsNullOrEmpty(msg.Content))
                {
                    string displayText = $"[{NAMES[i]}]: {msg.Content}";
                    if (Towel.AllTowel.TryGetValue(i + 1, out var towel) && towel != null)
                    {
                        bool accepted = towel.Say(displayText);
                        if (!accepted)
                            print($"[AIAgent] {NAMES[i]} message rejected (previous still playing)");
                    }
                }
            }

            ResumeCapture();
            _isWaiting = false;
        }
    }

    private void PauseCapture()
    {
        if (_capture != null && _capture.IsCapturing() && !_capture.IsPaused())
        {
            _capture.PauseCapture();
            print("[AIAgent] Capture paused");
        }
    }

    private void ResumeCapture()
    {
        if (_capture != null && _capture.IsCapturing() && _capture.IsPaused())
        {
            _capture.ResumeCapture();
            print("[AIAgent] Capture resumed");
        }
    }

    private async Task<string> SendAIRequest(int aiIndex)
    {
        var history = _histories[aiIndex];

        if (history.Count == 0)
        {
            history.Add(new ChatMessage
            {
                Role = "system",
                Content = PERSONALITIES[aiIndex] +
                    "\n\n你可以使用 shout_to_other 工具向其他AI喊话。每轮你都会被要求发言。保持每次发言在30字以内。"
            });
        }

        string userPrompt = _round == 1
            ? $"游戏开始！你是{NAMES[aiIndex]}，请第一个发言。可以对其他AI喊话。"
            : $"轮到你发言了（第{_round}轮）。请简短发言，可以对其他AI喊话。";

        history.Add(new ChatMessage { Role = "user", Content = userPrompt });

        var requestBody = new DeepSeekRequest
        {
            Model = MODEL,
            Messages = history.ToArray(),
            Tools = new[]
            {
                new Tool
                {
                    Type = "function",
                    Function = new ToolFunction
                    {
                        Name = "shout_to_other",
                        Description = "向另一个AI喊话，内容会显示在对方头顶。用于挑衅、结盟、威胁等。",
                        Parameters = new ToolParameters
                        {
                            Type = "object",
                            Properties = new Dictionary<string, ParamProperty>
                            {
                                ["target"] = new() { Type = "integer", Description = "目标AI编号(1-4)，不能是自己" },
                                ["message"] = new() { Type = "string", Description = "喊话内容，最多20字" }
                            },
                            Required = new[] { "target", "message" }
                        }
                    }
                }
            },
            Temperature = 1.2f,
            MaxTokens = 256,
            Stream = false
        };

        string json = JsonConvert.SerializeObject(requestBody, JSON_SETTINGS);

        using var req = new UnityWebRequest(API_URL, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Accept", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        req.SendWebRequest();
        while (!req.isDone)
            await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[AIAgent] {NAMES[aiIndex]} request failed: {req.error}");
            return null;
        }

        string responseText = req.downloadHandler.text;

        var msg = ParseResponseMessage(responseText);
        if (msg != null)
        {
            var assistantMsg = new ChatMessage { Role = "assistant" };
            if (!string.IsNullOrEmpty(msg.Content))
                assistantMsg.Content = msg.Content;
            if (msg.ToolCalls != null && msg.ToolCalls.Length > 0)
            {
                assistantMsg.ToolCalls = msg.ToolCalls;
                foreach (var tc in msg.ToolCalls)
                {
                    string toolResult = ExecuteToolCallAndGetResult(aiIndex, tc);
                    history.Add(assistantMsg);
                    history.Add(new ChatMessage { Role = "tool", ToolCallId = tc.Id, Content = toolResult });
                }
            }
            else
            {
                history.Add(assistantMsg);
            }
        }

        return responseText;
    }

    private ParsedMessage ParseResponseMessage(string json)
    {
        try
        {
            var resp = JsonConvert.DeserializeObject<ParsedResponse>(json, JSON_SETTINGS);
            if (resp?.Choices == null || resp.Choices.Length == 0)
                return null;
            return resp.Choices[0].Message;
        }
        catch (Exception e)
        {
            Debug.LogError($"[AIAgent] Failed to parse response: {e.Message}\n{json}");
            return null;
        }
    }

    private void ExecuteToolCall(int callerIndex, ToolCall tc)
    {
        string result = ExecuteToolCallAndGetResult(callerIndex, tc);
        print($"[AIAgent] Tool: {NAMES[callerIndex]} -> {result}");
    }

    private string ExecuteToolCallAndGetResult(int callerIndex, ToolCall tc)
    {
        if (tc.Function.Name != "shout_to_other")
            return $"unknown tool: {tc.Function.Name}";

        try
        {
            var args = JsonConvert.DeserializeObject<ShoutArgs>(tc.Function.Arguments, JSON_SETTINGS);
            if (args.Target < 1 || args.Target > 4 || args.Target == callerIndex + 1)
                return $"invalid target: {args.Target}";

            string msg = $"[{NAMES[callerIndex]} 喊话]: {args.Message}";
            if (Towel.AllTowel.TryGetValue(args.Target, out var towel))
            {
                bool accepted = towel.Say(msg);
                return accepted ? $"shouted to {args.Target}号" : $"shout to {args.Target}号 rejected (busy)";
            }
            return $"target {args.Target} not found";
        }
        catch (Exception e)
        {
            return $"parse error: {e.Message}";
        }
    }

    private class DeepSeekRequest
    {
        public string Model { get; set; }
        public ChatMessage[] Messages { get; set; }
        public Tool[] Tools { get; set; }
        public float Temperature { get; set; }
        public int MaxTokens { get; set; }
        public bool Stream { get; set; }
    }

    private class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string ToolCallId { get; set; }
        public ToolCall[] ToolCalls { get; set; }
    }

    private class Tool
    {
        public string Type { get; set; }
        public ToolFunction Function { get; set; }
    }

    private class ToolFunction
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ToolParameters Parameters { get; set; }
    }

    private class ToolParameters
    {
        public string Type { get; set; }
        public Dictionary<string, ParamProperty> Properties { get; set; }
        public string[] Required { get; set; }
    }

    private class ParamProperty
    {
        public string Type { get; set; }
        public string Description { get; set; }
    }

    private class ToolCall
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public ToolCallFunction Function { get; set; }
    }

    private class ToolCallFunction
    {
        public string Name { get; set; }
        public string Arguments { get; set; }
    }

    private class ParsedResponse
    {
        public Choice[] Choices { get; set; }
    }

    private class Choice
    {
        public ParsedMessage Message { get; set; }
    }

    private class ParsedMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public ToolCall[] ToolCalls { get; set; }
    }

    private class ShoutArgs
    {
        public int Target { get; set; }
        public string Message { get; set; }
    }
}
