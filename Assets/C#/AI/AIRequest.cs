using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using Newtonsoft;
using Newtonsoft.Json;
using UnityEngine.Networking;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Serialization;

#region 网络
public class StreamDownloadHandler : DownloadHandlerScript
{
    private Action<string> onDataReceived;
    private Action<string> onError;
    private Action onComplete;
    private System.Text.StringBuilder buffer = new System.Text.StringBuilder();
    private readonly System.Text.Decoder utf8Decoder = System.Text.Encoding.UTF8.GetDecoder();

    public StreamDownloadHandler(Action<string> onDataReceived, Action<string> onError, Action onComplete)
        : base(new byte[4096]) // 4KB 缓冲区
    {
        this.onDataReceived = onDataReceived;
        this.onError = onError;
        this.onComplete = onComplete;
    }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        if (data == null || dataLength == 0) return false;

        // 将接收到的字节转换为字符串
        // 使用 Decoder 跨包累积解码，避免 UTF-8 多字节字符被 4KB 分包切断
        char[] chars = new char[utf8Decoder.GetCharCount(data, 0, dataLength)];
        int charCount = utf8Decoder.GetChars(data, 0, dataLength, chars, 0);
        buffer.Append(chars, 0, charCount);

        // 按行处理数据
        string currentBuffer = buffer.ToString();
        int lineEnd;
        while ((lineEnd = currentBuffer.IndexOf('\n')) >= 0)
        {
            string line = currentBuffer.Substring(0, lineEnd).Trim();
            currentBuffer = currentBuffer.Substring(lineEnd + 1);

            if (line.StartsWith("data: "))
            {
                string jsonData = line.Substring(6);
                if (jsonData == "[DONE]")
                {
                    //onComplete?.Invoke();
                }
                else
                {
                    onDataReceived?.Invoke(jsonData);
                }
            }
        }

        buffer.Clear();
        buffer.Append(currentBuffer);

        return true;
    }

    protected override void CompleteContent()
    {
        // 处理最后可能剩余的缓冲数据
        if (buffer.Length > 0)
        {
            string remaining = buffer.ToString();
            if (remaining.StartsWith("data: "))
            {
                string jsonData = remaining.Substring(6);
                if (jsonData != "[DONE]")
                {
                    onDataReceived?.Invoke(jsonData);
                }
            }
        }
        // 流结束，清掉解码器残留状态（不完整的尾部字节按无效丢弃）
        utf8Decoder.Reset();
        onComplete?.Invoke();
    }
}
#endregion

#region 发送
public class DeepSeekMessage
{
    [JsonProperty("role")]
    public string role;

    [JsonProperty("content")]
    public string content;

    [JsonProperty("reasoning_content", NullValueHandling = NullValueHandling.Ignore)]
    public string reasoning_content;

    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string name;

    [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
    public string tool_call_id;

    // 直接使用公共字段，配合ShouldSerialize
    [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
    public List<ToolCall> tool_calls;

    [JsonConstructor]
    public DeepSeekMessage() { }

    public DeepSeekMessage(string role, string content)
    {
        this.role = role;
        this.content = content;
    }

    [JsonIgnore]
    public DeepSeekMessage Clone { get => JsonConvert.DeserializeObject<DeepSeekMessage>(JsonConvert.SerializeObject(this)); }

    public bool ShouldSerializename()
    {
        // 只有当name有值且角色不是"tool"时才序列化
        return !string.IsNullOrEmpty(name) && role != "tool";
    }
    // 关键：这个方法告诉Newtonsoft.Json何时序列化tool_calls
    public bool ShouldSerializetool_calls()
    {
        return tool_calls != null && tool_calls.Count > 0;
    }
    public bool ShouldSerializetool_call_id()
    {
        return !string.IsNullOrEmpty(tool_call_id);
    }
}

// 同时修正ToolCalls类（注意大小写和字段名）
[Serializable]
public class ToolCall
{
    [JsonProperty("index")]
    public int index;
    [JsonProperty("id")]
    public string id;

    [JsonProperty("type")]
    public string type = "function";

    [JsonProperty("function")]
    public FunctionCall function;
}

[Serializable]
public class FunctionCall
{
    [JsonProperty("name")]
    public string name;

    [JsonProperty("arguments")]
    public string arguments;
}
[Serializable]
public class DeepSeekRequest
{
    public string model = "deepseek-flash";
    public double temperature = 0.7;
    public int max_tokens = 2048;
    public bool stream = false;
    public string reasoning_effort;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThinkingConfig thinking = new(true);

    public bool ShouldSerializethinking()
    {
        // enabled / disabled 都需要显式发送，否则模型可能走默认开启思考
        return thinking != null;
    }

    public bool ShouldSerializereasoning_effort()
    {
        return !string.IsNullOrEmpty(reasoning_effort);
    }

    [HideInInspector]
    public List<DeepSeekMessage> messages;
    public List<Tool> tools = new();
    /// <summary>支持字符串（auto/none/required）或对象（强制指定某个 function）。</summary>
    public object tool_choice = "auto";
    //新词度-2~2
    public float frequency_penalty = 0f;
    //新话题度-2~2
    public float presence_penalty = 0f;

    public DeepSeekRequest()
    { }

    public DeepSeekRequest DeepCopy()
    {
        // 使用 Newtonsoft.Json 深拷贝（独立副本，修改不影响原对象）
        string json = JsonConvert.SerializeObject(this);
        return JsonConvert.DeserializeObject<DeepSeekRequest>(json);
    }
}
#endregion

#region 接收

public interface Itool
{
    System.Threading.Tasks.Task<List<DeepSeekMessage>> DealToolCallsAsync(List<ToolCall> toolCalls, int stage = -1);
}

// 添加ThinkingConfig类
[Serializable]
public class ThinkingConfig
{
    public string type;
    public ThinkingConfig(bool t)
    {
        type = t ? "enabled" : "disabled";
    }
    public bool ToBool()
    {
        return type == "enabled";
    }
}

// 新增：Tool类定义
[Serializable]
public class Tool
{
    public string type = "function";
    public Function function;
}

[Serializable]
public class Function
{
    public string name;
    public string description;
    public object parameters;
}

[System.Serializable]
public class DeepSeekResponse
{
    public Choice[] choices;

    [System.Serializable]
    public class Choice
    {
        public Message message;

        [System.Serializable]
        public class Message
        {
            public string role;
            public string reasoning_content;
            public string content;
            public List<ToolCall> tool_calls;
        }
    }
}

[System.Serializable]
public class DeepSeekChunk
{
    public string id;
    public string @object;  // 注意：object 是 C# 关键字，用 @ 转义
    public int created;
    public string model;
    public string system_fingerprint;
    public Choice[] choices;

    [System.Serializable]
    public class Choice
    {
        public int index;
        public Delta delta;  // 流式响应用的是 delta，不是 message
        public string finish_reason;
        public object logprobs;  // 通常为 null
    }

    [System.Serializable]
    public class Delta
    {
        public string role;  // 仅第一个 chunk 有
        public string content;
        public string reasoning_content;
        public List<ToolCall> tool_calls;  // 如果有工具调用
    }
}



#endregion

#region 转接
public class RequestInfo
{
    public string apiUrl = "https://api.deepseek.com/v1/chat/completions";
    public string apiKey = ""; //API密钥

    public DeepSeekRequest request;
    public List<DeepSeekMessage> messages => request.messages;

    public Action<List<DeepSeekMessage>> onResponse;
    public Action<string> onError;

    public Itool toolkit;
    public int toolStage = -1;
    public bool back_tool = true;

    /// <summary>
    /// 回复落库/执行工具前的外部校验钩子（由 AIAgent 注入）。
    /// 参数：本次 RequestInfo、候选 assistant 回复、该候选是否已经作为占位消息写入 history。
    /// 返回 true 表示通过并继续流程；返回 false 表示本次响应已由外部处理（重发或报错），AIRequest 停止当前流水线。
    /// </summary>
    public Func<DeepSeekMessage, bool> validateAndMaybeRetry;

    public void AddMessage(List<DeepSeekMessage> message)
    {
        request.messages.AddRange(message);
    }
    public void AddMessage(DeepSeekMessage message)
    {
        request.messages.Add(message);
    }
    /// <summary>
    /// 注意request是实际发送的messages和messages不同
    /// </summary>
    /// <param name="request"></param>
    /// <param name="onResponse"></param>
    /// <param name="onError"></param>
    /// <param name="toolkit"></param>
    /// <param name="back_tool"></param>
    public RequestInfo(DeepSeekRequest request, Action<List<DeepSeekMessage>> onResponse, Action<string> onError, Itool toolkit = null, bool back_tool = true, int toolStage = -1)
    {
        this.request = request;
        this.onResponse = onResponse;
        this.onError = onError;
        this.toolkit = toolkit;
        this.back_tool = back_tool;
        this.toolStage = toolStage;
    }
}

#endregion

#region Token计算
/// <summary>
/// DeepSeek V3 BPE Tokenizer — 精确 token 计数
/// 基于 tokenizer.json (LlamaTokenizerFast / BPE)
/// </summary>
public class DeepSeekTokenizer
{
    private Dictionary<string, int> vocab;
    // merges: (id1, id2) → (merged_id, rank) where lower rank = higher priority
    private Dictionary<(int, int), (int mergedId, int rank)> merges;
    private string[] idToToken;

    // ByteLevel 映射：byte → char
    private static readonly char[] byteToChar = new char[256];
    private static readonly Dictionary<char, byte> charToByte = new();

    // 预分割正则
    private static readonly Regex[] preSplitPatterns;

    static DeepSeekTokenizer()
    {
        BuildByteLevelMap();
        preSplitPatterns = new[]
        {
            new Regex(@"\p{N}{1,3}", RegexOptions.Compiled),
            new Regex(@"[\u4e00-\u9fa5\u3040-\u309f\u30a0-\u30ff]+", RegexOptions.Compiled),
            new Regex(@"[!""#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~][A-Za-z]+|[^\r\n\p{L}\p{P}\p{S}]?[\p{L}\p{M}]+| ?[\p{P}\p{S}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+", RegexOptions.Compiled),
        };
    }

    public DeepSeekTokenizer(string jsonPath)
    {
        Load(File.ReadAllText(jsonPath));
    }

    public DeepSeekTokenizer(byte[] jsonBytes)
    {
        Load(Encoding.UTF8.GetString(jsonBytes));
    }

    private void Load(string json)
    {
        var data = JsonConvert.DeserializeObject<TokenizerData>(json);
        if (data?.model == null) throw new Exception("Invalid tokenizer.json");

        var modelVocab = data.model.vocab;
        var modelMerges = data.model.merges;
        int vocabSize = modelVocab.Count;

        // vocab: token → id
        vocab = new Dictionary<string, int>(vocabSize);
        idToToken = new string[vocabSize];
        foreach (var kv in modelVocab)
        {
            vocab[kv.Key] = kv.Value;
            idToToken[kv.Value] = kv.Key;
        }

        // merges: (id1, id2) → (merged_id, rank) with priority order
        merges = new Dictionary<(int, int), (int, int)>(modelMerges.Count);
        for (int rank = 0; rank < modelMerges.Count; rank++)
        {
            string mergeStr = modelMerges[rank];
            int spaceIdx = mergeStr.IndexOf(' ');
            if (spaceIdx < 0) continue;
            string t1 = mergeStr.Substring(0, spaceIdx);
            string t2 = mergeStr.Substring(spaceIdx + 1);
            if (vocab.TryGetValue(t1, out int id1) &&
                vocab.TryGetValue(t2, out int id2) &&
                vocab.TryGetValue(mergeStr.Replace(" ", ""), out int mergedId))
            {
                merges[(id1, id2)] = (mergedId, rank);
            }
        }
    }

    /// <summary>
    /// 精确计算 token 数量
    /// </summary>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Encode(text).Count;
    }

    /// <summary>
    /// 编码文本为 token ID 列表
    /// </summary>
    public List<int> Encode(string text)
    {
        var tokens = new List<int>();

        // Step 1: Pre-tokenize — apply split patterns
        var segments = PreTokenize(text);

        // Step 2: ByteLevel + BPE for each segment
        foreach (string segment in segments)
        {
            // ByteLevel: convert segment to UTF-8 bytes → map to unicode chars
            string byteEncoded = BytesToUnicode(Encoding.UTF8.GetBytes(segment));

            // BPE encode
            var bpeIds = BPEEncode(byteEncoded);
            tokens.AddRange(bpeIds);
        }

        return tokens;
    }

    public string DebugEncode(string text)
    {
        var sb = new StringBuilder();
        var segments = PreTokenize(text);
        sb.AppendLine($"=== Encode: '{text}' === ({segments.Count} segments)");
        foreach (string seg in segments)
        {
            string be = BytesToUnicode(Encoding.UTF8.GetBytes(seg));

            // Show initial char IDs
            sb.Append($"  [{seg}] -> byte:[{be}] -> chars:[");
            for (int i = 0; i < be.Length; i++)
            {
                string ch = be[i].ToString();
                if (vocab.TryGetValue(ch, out int cid))
                    sb.Append($"{ch}({cid})");
                else
                    sb.Append($"{ch}(?)");
                if (i < be.Length - 1) sb.Append(",");
            }
            sb.AppendLine("]");

            var ids = BPEEncode(be);
            var tokenStrs = new List<string>();
            foreach (int id in ids)
                tokenStrs.Add(id >= 0 && id < idToToken.Length ? idToToken[id] : $"???({id})");
            sb.AppendLine($"    -> merged: {string.Join("|", tokenStrs)}");
        }
        sb.AppendLine($"Total tokens: {CountTokens(text)}");
        return sb.ToString();
    }

    public List<string> PreTokenize(string text)
    {
        var segments = new List<string> { text };

        foreach (var pattern in preSplitPatterns)
        {
            var next = new List<string>();
            foreach (string seg in segments)
            {
                int lastEnd = 0;
                foreach (Match m in pattern.Matches(seg))
                {
                    if (m.Index > lastEnd)
                        next.Add(seg.Substring(lastEnd, m.Index - lastEnd));
                    next.Add(m.Value);
                    lastEnd = m.Index + m.Length;
                }
                if (lastEnd < seg.Length)
                    next.Add(seg.Substring(lastEnd));
            }
            segments = next;
        }

        return segments;
    }

    private List<int> BPEEncode(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<int>();

        // Start with individual characters
        var ids = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            if (vocab.TryGetValue(ch, out int id))
                ids.Add(id);
            else
                ids.Add(-1);
        }

        // Apply merge rules by priority (lowest rank = highest priority)
        while (ids.Count > 1)
        {
            int bestRank = int.MaxValue;
            int bestIdx = -1;
            int bestMergedId = -1;

            for (int i = 0; i < ids.Count - 1; i++)
            {
                if (merges.TryGetValue((ids[i], ids[i + 1]), out var entry))
                {
                    if (entry.rank < bestRank)
                    {
                        bestRank = entry.rank;
                        bestMergedId = entry.mergedId;
                        bestIdx = i;
                    }
                }
            }

            if (bestIdx < 0) break;

            ids[bestIdx] = bestMergedId;
            ids.RemoveAt(bestIdx + 1);
        }

        return ids;
    }

    #region ByteLevel Mapping

    private static void BuildByteLevelMap()
    {
        // Standard GPT-2 ByteLevel mapping
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if ((b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF))
            {
                byteToChar[b] = (char)b;
            }
            else
            {
                byteToChar[b] = (char)(256 + n);
                n++;
            }
            charToByte[byteToChar[b]] = (byte)b;
        }
    }

    private static string BytesToUnicode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            sb.Append(byteToChar[b]);
        return sb.ToString();
    }

    #endregion

    #region JSON Model

    [Serializable]
    class TokenizerData
    {
        public ModelData model;
    }

    [Serializable]
    class ModelData
    {
        public Dictionary<string, int> vocab;
        public List<string> merges;
    }

    #endregion
}

#endregion

/// <summary>
/// 包含给AI发消息的基础支持
/// </summary>
public static class AIRequest
{
    /// <summary>
    /// 上下文容量
    /// </summary>
    private static int MaxTokens = 1048576;
    private static readonly JsonSerializerSettings json_serializer_settings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        ContractResolver = new DefaultContractResolver{ NamingStrategy = null }  // 保持原字段名
    };

    public static List<DeepSeekMessage> CloneML(List<DeepSeekMessage> input)
    {
        return JsonConvert.DeserializeObject<List<DeepSeekMessage>>(JsonConvert.SerializeObject(input));
    }

    public static void SendRequest(RequestInfo requestInfo)
    {
        // 在发送副本上做字段裁剪，绝不修改 CharacterCard 的 history。
        // 否则每次发送都会把历史 assistant 消息的 reasoning_content 清空，
        // 保存时只会剩下最后一条思考。
        DeepSeekRequest outgoingRequest = requestInfo.request.DeepCopy();

        // 非 DeepSeek 模型去掉专属/不支持字段
        bool isDeepSeek = outgoingRequest.model.ToLower().Contains("deepseek");
        if (!isDeepSeek)
        {
            outgoingRequest.thinking = null;
            outgoingRequest.reasoning_effort = null;
            outgoingRequest.tools = null;
            outgoingRequest.tool_choice = null;
        }

        // 统一 reasoning_content：thinking 开启时补空，关闭时清掉（只改发送副本）
        bool thinkingOn = outgoingRequest.thinking != null && outgoingRequest.thinking.type == "enabled";
        foreach (var msg in outgoingRequest.messages)
        {
            if (msg.role == "assistant")
            {
                if (thinkingOn && string.IsNullOrEmpty(msg.reasoning_content))
                    msg.reasoning_content = "";
                else if (!thinkingOn)
                    msg.reasoning_content = null;
            }
        }

        string jsonData = JsonConvert.SerializeObject(outgoingRequest, json_serializer_settings);

        //判断流式
        if (requestInfo.request.stream)
        {
            _ = SendStreamAsync(jsonData, requestInfo);
        }
        else
        {
            _ = SendAsync(jsonData, requestInfo);
        }
    }

    //非流式
    private static async Task SendAsync(string jsonData, RequestInfo requestInfo)
    {
        // 创建UnityWebRequest
        var url = requestInfo.apiUrl;
        var key = requestInfo.apiKey;
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", $"Bearer {key}");

        // 发送请求
        request.SendWebRequest();
        while (!request.isDone) await Task.Yield();

        // 处理响应
        if (request.result == UnityWebRequest.Result.Success)
        {
            string responseJson = request.downloadHandler.text;
            var response = JsonUtility.FromJson<DeepSeekResponse>(responseJson);

            if (response.choices != null && response.choices.Length > 0)
            {
                var back_message = response.choices[0].message;
                string aiResponse = back_message.content;
                string after_thinking = back_message.reasoning_content;

                var assistant = new DeepSeekMessage()
                {
                    role = "assistant",
                    reasoning_content = after_thinking,
                    content = aiResponse,
                    tool_calls = back_message.tool_calls
                };

                // 回复校验交给外部（AIAgent）处理；返回 false 表示已拦截或重发，停止本次响应
                if (requestInfo.validateAndMaybeRetry != null &&
                    !requestInfo.validateAndMaybeRetry(assistant))
                {
                    request.Dispose();
                    return;
                }

                if (assistant.tool_calls != null && assistant.tool_calls.Count > 0)
                {
                    // 把AI的回复加入对话记录
                    requestInfo.AddMessage(assistant);

                    if (requestInfo.toolkit != null)
                    {
                        var newMessages = await requestInfo.toolkit.DealToolCallsAsync(assistant.tool_calls, requestInfo.toolStage);
                        if (newMessages != null && newMessages.Count > 0)
                        {
                            if (requestInfo.back_tool)
                            {
                                requestInfo.AddMessage(newMessages);
                                SendRequest(requestInfo);
                            }
                            else
                            {
                                requestInfo.onResponse?.Invoke(newMessages);
                            }
                        }
                    }
                    else
                    {
                        Debug.LogError("useTool 未初始化");
                        requestInfo.onError?.Invoke("工具处理器未初始化");
                    }
                }
                else
                {
                    // 把AI的回复加入对话记录
                    requestInfo.AddMessage(assistant);
                    requestInfo.onResponse?.Invoke(new() { assistant.Clone });
                }
            }
            else
            {
                requestInfo.onError?.Invoke("No response from AI");
            }
        }
        else
        {
            requestInfo.onError?.Invoke($"API Error: {request.error}");
        }
        request.Dispose();
    }

    private static async Task SendStreamAsync(string jsonData, RequestInfo requestInfo)
    {
        var url = requestInfo.apiUrl;
        var key = requestInfo.apiKey;
        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonData);

        StringBuilder final_content = new StringBuilder();
        StringBuilder final_thinking = new StringBuilder();
        Dictionary<int, StringBuilder> toolCallArguments = new Dictionary<int, StringBuilder>();
        Dictionary<int, ToolCall> toolCalls = new Dictionary<int, ToolCall>();
        bool hasToolCalls = false;

        requestInfo.AddMessage(new DeepSeekMessage() { role = "assistant", reasoning_content = final_thinking.ToString(), content = final_content.ToString() });

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new StreamDownloadHandler(
            onDataReceived: (jsonData) => {
                try
                {
                    var chunk = JsonConvert.DeserializeObject<DeepSeekChunk>(jsonData);
                    if (chunk.choices != null && chunk.choices.Length > 0)
                    {
                        var delta = chunk.choices[0].delta;
                        var finish_reason = chunk.choices[0].finish_reason;

                        if (!string.IsNullOrEmpty(delta.content))
                        {
                            final_content.Append(delta.content);
                            requestInfo.messages[^1].content = final_content.ToString();
                        }

                        if (!string.IsNullOrEmpty(delta.reasoning_content))
                        {
                            final_thinking.Append(delta.reasoning_content);
                            requestInfo.messages[^1].reasoning_content = final_thinking.ToString();
                        }

                        if (delta.tool_calls != null && delta.tool_calls.Count > 0)
                        {
                            hasToolCalls = true;
                            foreach (var tc in delta.tool_calls)
                            {
                                int index = tc.index;
                                if (!toolCalls.ContainsKey(index))
                                {
                                    toolCalls[index] = new ToolCall
                                    {
                                        id = tc.id,
                                        type = tc.type,
                                        function = new FunctionCall
                                        {
                                            name = tc.function?.name ?? "",
                                            arguments = ""
                                        }
                                    };
                                    toolCallArguments[index] = new StringBuilder();
                                }

                                if (tc.function != null && !string.IsNullOrEmpty(tc.function.arguments))
                                {
                                    toolCallArguments[index].Append(tc.function.arguments);
                                    toolCalls[index].function.arguments = toolCallArguments[index].ToString();
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"解析chunk失败: {e.Message}\n数据: {jsonData}");
                    requestInfo.onError?.Invoke($"解析chunk失败: {e.Message}");
                }
            },
            onError: (error) => {
                requestInfo.onError?.Invoke(error);
            },
            onComplete: () => {
            }
        );

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", $"Bearer {key}");

        request.SendWebRequest();
        while (!request.isDone) await Task.Yield();

        if (request.result != UnityWebRequest.Result.Success)
        {
            requestInfo.onError?.Invoke($"API Error: {request.error}");
            request.Dispose();
            return;
        }

        if (hasToolCalls && toolCalls.Count > 0)
        {
            var toolCallsList = toolCalls.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            var assistantMessage = new DeepSeekMessage() {
                role = "assistant",
                reasoning_content = final_thinking.ToString(),
                content = final_content.ToString(),
                tool_calls = toolCallsList
            };

            if (requestInfo.messages.Count > 0 && requestInfo.messages[^1].role == "assistant")
            {
                requestInfo.messages[^1] = assistantMessage;
            }

            // 回复校验交给外部（AIAgent）处理；返回 false 表示已拦截或重发，停止本次响应
            if (requestInfo.validateAndMaybeRetry != null &&
                !requestInfo.validateAndMaybeRetry(assistantMessage))
            {
                request.Dispose();
                return;
            }

            if (requestInfo.toolkit != null)
            {
                var newMessages = await requestInfo.toolkit.DealToolCallsAsync(toolCallsList, requestInfo.toolStage);
                if (newMessages != null && newMessages.Count > 0)
                {
                    if (requestInfo.back_tool)
                    {
                        requestInfo.AddMessage(newMessages);
                        SendRequest(requestInfo);
                    }
                    else
                    {
                        requestInfo.onResponse?.Invoke(null);
                    }
                }
            }
            else
            {
                Debug.LogError("useTool 未初始化");
                requestInfo.onError?.Invoke("工具处理器未初始化");
            }
        }
        else
        {
            var assistantMessage = new DeepSeekMessage() {
                role = "assistant",
                reasoning_content = final_thinking.ToString(),
                content = final_content.ToString()
            };

            if (requestInfo.messages.Count > 0 && requestInfo.messages[^1].role == "assistant")
            {
                requestInfo.messages[^1] = assistantMessage;
            }

            // 回复校验交给外部（AIAgent）处理；返回 false 表示已拦截或重发，停止本次响应
            if (requestInfo.validateAndMaybeRetry != null &&
                !requestInfo.validateAndMaybeRetry(assistantMessage))
            {
                request.Dispose();
                return;
            }

            requestInfo.onResponse?.Invoke(new() { assistantMessage.Clone });
        }

        toolCallArguments.Clear();
        toolCalls.Clear();
        request.Dispose();
    }

    /// <summary>
    /// 限制上下文长度
    /// </summary>
    /// <param name="_message">完整消息</param>
    /// <param name="maxTokens">值为-1时为默认MaxToken</param>
    /// <param name="systemMessage">不写默认原始系统消息</param>
    /// <param name="characterSetting">不写默认原始角色设定消息</param>
    /// <returns></returns>
    public static List<DeepSeekMessage> GetOptimizedContext(List<DeepSeekMessage> _message, bool thinking, int maxTokens = -1)
    {
        //MaxTokens == -2 代表全部取用
        if (maxTokens == -1) maxTokens = MaxTokens;

        List<DeepSeekMessage> optimized = new List<DeepSeekMessage>();
        int estimatedTokens = 0;

        // 用于跟踪需要寻找的assistant消息（可能有多个tool调用）
        HashSet<string> pendingToolCallIds = new HashSet<string>();
        bool needToFindAssistant = false;

        int system_messageCount = 0;

        // 从最新消息开始添加（倒序遍历）
        for (int i = _message.Count - 1; i >= 0; i--)
        {
            var msg = new DeepSeekMessage()
            {
                role = _message[i].role,
                content = _message[i].content,
                reasoning_content = _message[i].reasoning_content,
                name = _message[i].name,
                tool_call_id = _message[i].tool_call_id,
                tool_calls = _message[i].tool_calls,
            };

            int messageTokens = TokenEstimator.EstimateTokensCached(msg.content) + TokenEstimator.EstimateTokensCached(msg.reasoning_content);

            // 如果是tool消息，记录需要寻找对应的assistant
            if (msg.role == "tool" && !string.IsNullOrEmpty(msg.tool_call_id))
            {
                needToFindAssistant = true;
                pendingToolCallIds.Add(msg.tool_call_id);
                optimized.Insert(system_messageCount, msg);
                estimatedTokens += messageTokens;
                continue;
            }

            // 正在寻找对应的assistant时
            if (needToFindAssistant)
            {
                bool isTargetAssistant = msg.role == "assistant" && msg.tool_calls != null;

                if (isTargetAssistant)
                {
                    var calledToolIds = msg.tool_calls
                        .Where(tc => !string.IsNullOrEmpty(tc.id))
                        .Select(tc => tc.id)
                        .ToList();

                    bool hasPendingTool = calledToolIds.Any(id => pendingToolCallIds.Contains(id));

                    if (hasPendingTool)
                    {
                        optimized.Insert(system_messageCount, msg);
                        estimatedTokens += messageTokens;

                        foreach (var toolId in calledToolIds)
                        {
                            pendingToolCallIds.Remove(toolId);
                        }

                        if (pendingToolCallIds.Count == 0)
                        {
                            needToFindAssistant = false;
                        }
                    }
                    else
                    {
                        // 不是我们要找的assistant，但仍需保留（对话完整性）
                        optimized.Insert(system_messageCount, msg);
                        estimatedTokens += messageTokens;
                    }
                }
                else
                {
                    // 还没找到对应的assistant，保留中间消息
                    optimized.Insert(system_messageCount, msg);
                    estimatedTokens += messageTokens;
                }
                continue;
            }

            // 正常情况：检查token限制
            if (estimatedTokens + messageTokens <= maxTokens || maxTokens == -2)
            {
                optimized.Insert(system_messageCount, msg);
                estimatedTokens += messageTokens;
            }
            else
            {
                break;
            }
        }

        // ==================== 修复点：清理孤儿tool消息 ====================
        if (pendingToolCallIds.Count > 0)
        {
            optimized.RemoveAll(m =>
                m.role == "tool" &&
                !string.IsNullOrEmpty(m.tool_call_id) &&
                pendingToolCallIds.Contains(m.tool_call_id));

            estimatedTokens = optimized.Sum(m => TokenEstimator.EstimateTokensCached(m.content));
        }

        return optimized;
    }
    /// <summary>
    /// 插入系统消息
    /// </summary>
    /// <param name="optimized">输入的列表</param>
    /// <param name="all">要插入的系统消息</param>
    /// <param name="lastcount">导数第几个插入</param>
    public static void InsertSystemMessages(List<DeepSeekMessage> optimized, List<string> all)
    {
        for (int i = all.Count - 1; i > -1; i--)
        {
            optimized.Insert(0, new DeepSeekMessage("system", all[i]));
        }
    }
    public static void InsertUserMessages(List<DeepSeekMessage> optimized, List<string> all, int index = 0)
    {
        for (int i = all.Count - 1; i > -1; i--)
        {
            optimized.Insert(index, new DeepSeekMessage("user", all[i]));
        }
    }
    public static class TokenEstimator
    {
        private static DeepSeekTokenizer _tokenizer;
        private static Dictionary<string, int> tokenCache = new Dictionary<string, int>();

        private static DeepSeekTokenizer GetTokenizer()
        {
            if (_tokenizer != null) return _tokenizer;
            string streamingPath = Path.Combine(Application.streamingAssetsPath, "tokenizer.json");
            string dataPath = Path.Combine(Application.dataPath, "tokenizer.json");
            string path = File.Exists(streamingPath) ? streamingPath : dataPath;
            if (File.Exists(path))
            {
                _tokenizer = new DeepSeekTokenizer(path);
                Debug.Log("[TokenEstimator] 已加载精确 tokenizer: " + path);
            }
            else
            {
                Debug.LogWarning("[TokenEstimator] tokenizer.json 未找到: " + streamingPath + " 或 " + dataPath);
                _tokenizer = null;
            }
            return _tokenizer;
        }

        public static int EstimateTokensCached(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            // 长文本不缓存，避免把历史/大段回复长期留在内存里
            if (text.Length > 512)
                return CountTokens(text);

            if (tokenCache.TryGetValue(text, out int cached))
                return cached;

            int tokens = CountTokens(text);
            if (tokenCache.Count >= 10000)
                tokenCache.Clear();
            tokenCache[text] = tokens;
            return tokens;
        }

        private static int CountTokens(string text)
        {
            var t = GetTokenizer();
            if (t != null)
            {
                try { return t.CountTokens(text); }
                catch (Exception e) { Debug.LogError($"[TokenEstimator] tokenizer 错误: {e.Message}"); }
            }
            return QuickEstimate(text);
        }

        private static int QuickEstimate(string text)
        {
            int chineseCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x4E00 && c <= 0x9FFF)
                    chineseCount++;
            }
            return (chineseCount * 5 + (text.Length - chineseCount)) / 2;
        }
    }
}
