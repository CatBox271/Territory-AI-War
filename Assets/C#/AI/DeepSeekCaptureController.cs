using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;
using RenderHeads.Media.AVProMovieCapture;

public class DeepSeekCaptureController : MonoBehaviour
{
    [SerializeField] private CaptureBase _capture;

    private string _apiKey;
    private bool _isWaiting;

    private const string API_URL = "https://api.deepseek.com/chat/completions";
    private const string MODEL = "deepseek-v4-flash";

    private static readonly JsonSerializerSettings JSON_SETTINGS = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
    };

    private void Awake()
    {
        var keyPath = Path.Combine(Application.persistentDataPath, "Key.txt");
        if (File.Exists(keyPath))
        {
            _apiKey = File.ReadAllText(keyPath).Trim();
            print($"[DeepSeek] API key loaded from {keyPath}");
        }
        else
        {
            Debug.LogError($"[DeepSeek] Key.txt not found at {keyPath}");
        }
    }

    [ContextMenu("Send Test Query")]
    public void SendTestQuery()
    {
        SendTestQueryAsync();
    }

    public async void SendTestQueryAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            Debug.LogError("[DeepSeek] No API key loaded");
            return;
        }

        if (_capture == null)
        {
            Debug.LogError("[DeepSeek] Capture component not assigned");
            return;
        }

        if (_isWaiting)
        {
            Debug.LogWarning("[DeepSeek] Already waiting for a response");
            return;
        }

        _isWaiting = true;

        if (_capture.IsCapturing() && !_capture.IsPaused())
        {
            _capture.PauseCapture();
            print("[DeepSeek] Capture paused, sending request...");
        }
        else
        {
            print("[DeepSeek] Capture not active, sending request anyway...");
        }

        string response = await SendDeepSeekRequest(BuildComplexQuery());

        if (_capture.IsCapturing() && _capture.IsPaused())
        {
            _capture.ResumeCapture();
            print("[DeepSeek] Capture resumed");
        }

        print($"[DeepSeek] Response received ({response.Length} chars)");
        _isWaiting = false;
    }

    private async Task<string> SendDeepSeekRequest(string userMessage)
    {
        var payload = new
        {
            model = MODEL,
            messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant." },
                new { role = "user", content = userMessage }
            },
            thinking = new { type = "enabled" },
            reasoning_effort = "high",
            max_tokens = 4096,
            temperature = 1,
            top_p = 1
        };

        string json = JsonConvert.SerializeObject(payload, JSON_SETTINGS);

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
            Debug.LogError($"[DeepSeek] Request failed: {req.error}\n{req.downloadHandler.text}");
            return req.error;
        }

        return req.downloadHandler.text;
    }

    private static string BuildComplexQuery()
    {
        return @"你是一个实时战略游戏的AI决策引擎。请分析以下战局并给出最优策略：

当前战局：
- 我方兵力：1200 步兵（攻10/防5/速2），400 弓兵（攻15/防3/速3/射程5），200 骑兵（攻20/防8/速6）
- 敌方兵力：900 步兵（攻12/防6/速2），600 弓兵（攻14/防4/速3/射程4），350 骑兵（攻22/防7/速7）
- 地形：中央有一条河（减速50%），河上有两座桥；左侧为高地（弓兵射程+1）；右侧为森林（骑兵速度-2）
- 天气：当前晴天，3回合后预计下雨（全兵种速度-1，弓兵攻击-3）
- 我方资源：可招募 300 任意兵种，或升级一座箭塔（攻25/射程4），或召唤一次雷击（对3x3区域造成80伤害）
- 胜利条件：消灭敌方全部单位，或占领敌方大本营（需在敌方大本营相邻格停留1回合）

请详细分析：
1. 对敌方可能的3种战略做出预判
2. 针对每种敌方战略给出我方最优应对方案
3. 综合考虑给出全局最优策略，包括兵力部署、资源使用时机、关键节点的决策条件
4. 模拟推演前5回合的兵力变化

请用中文回复，尽量详细。";
    }
}
