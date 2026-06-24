using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityEngine.Networking;

public static class DeepSeekClient
{
    public static string ApiKey { get; set; }
    public static string Model { get; set; } = "deepseek-chat";
    public static string BaseUrl { get; set; } = "https://api.deepseek.com/chat/completions";

    private static readonly JsonSerializerSettings JSON_SETTINGS = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() }
    };

    /// <summary>从 persistentDataPath 加载 Key.txt</summary>
    public static bool LoadKeyFromFile(string filename = "Key.txt")
    {
        var path = Path.Combine(Application.persistentDataPath, filename);
        if (!File.Exists(path))
        {
            Debug.LogError($"[DeepSeek] {filename} not found at {path}");
            return false;
        }
        ApiKey = File.ReadAllText(path).Trim();
        Debug.Log($"[DeepSeek] API key loaded from {path}");
        return true;
    }

    /// <summary>简单对话</summary>
    public static Task<string> Chat(string userMessage, string systemPrompt = null)
    {
        var messages = new List<ChatMsg>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new ChatMsg { Role = "system", Content = systemPrompt });
        messages.Add(new ChatMsg { Role = "user", Content = userMessage });
        return Chat(messages);
    }

    /// <summary>完整对话（支持 tools、对话历史）</summary>
    public static async Task<string> Chat(List<ChatMsg> messages, ToolDef[] tools = null,
        float temperature = 1f, int maxTokens = 256)
    {
        var payload = new
        {
            model = Model,
            messages,
            tools,
            temperature,
            max_tokens = maxTokens,
            stream = false
        };

        string json = JsonConvert.SerializeObject(payload, JSON_SETTINGS);

        using var req = new UnityWebRequest(BaseUrl, "POST");
        byte[] body = Encoding.UTF8.GetBytes(json);
        req.uploadHandler = new UploadHandlerRaw(body);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Accept", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {ApiKey}");

        req.SendWebRequest();
        while (!req.isDone)
            await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[DeepSeek] Request failed: {req.error}\n{req.downloadHandler.text}");
            return null;
        }

        return req.downloadHandler.text;
    }

    /// <summary>从 API 返回的原始 JSON 中解析 assistant message</summary>
    public static ChatResponse ParseResponse(string json)
    {
        try
        {
            var resp = JsonConvert.DeserializeObject<RawResponse>(json, JSON_SETTINGS);
            if (resp?.Choices == null || resp.Choices.Length == 0)
                return null;
            return resp.Choices[0].Message;
        }
        catch (Exception e)
        {
            Debug.LogError($"[DeepSeek] Parse error: {e.Message}");
            return null;
        }
    }

    // ---- 类型 ----

    public class ChatMsg
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string ToolCallId { get; set; }
        public ToolCall[] ToolCalls { get; set; }
    }

    public class ToolDef
    {
        public string Type { get; set; } = "function";
        public FunctionDef Function { get; set; }
    }

    public class FunctionDef
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ParamDef Parameters { get; set; }
    }

    public class ParamDef
    {
        public string Type { get; set; } = "object";
        public System.Collections.Generic.Dictionary<string, PropDef> Properties { get; set; }
        public string[] Required { get; set; }
    }

    public class PropDef
    {
        public string Type { get; set; }
        public string Description { get; set; }
    }

    public class ToolCall
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public ToolFunc Function { get; set; }
    }

    public class ToolFunc
    {
        public string Name { get; set; }
        public string Arguments { get; set; }
    }

    public class ChatResponse
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public ToolCall[] ToolCalls { get; set; }
    }

    [Serializable]
    private class RawResponse
    {
        public Choice[] Choices { get; set; }
    }

    [Serializable]
    private class Choice
    {
        public ChatResponse Message { get; set; }
    }
}
