using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
// System.Diagnostics 里也有 Debug，这里明确指向 Unity 的（不然 CS0104 不明确引用）
using Debug = UnityEngine.Debug;

/// <summary>
/// Jev（TypeSafe System One）独立请求层 —— 只做判断、不生成文本，和 DeepSeek 的 AIRequest 互不干扰。
///
/// 三种题：
///   noul   = 是不是，返回 0~1 的概率
///   choice = 多选一，返回选中的选项 key + 全选项概率分布 + confidence
///   score  = 有序量表，返回各档概率加权平均分（小数，不是整数档）
///
/// state 放素材、questions 放判断，缺一不可；一次请求可以塞多道题（state 只算一次输入，能省往返）。
///
/// 用法：
///   var r = await JEVRequest.Ask(
///       new { 回复原文 = raw, 规则 = rules },
///       new Dictionary<string, JEVRequest.JEVQuestion>
///       {
///           ["missing_seg"] = JEVRequest.JEVQuestion.Noul("【分析】【我要】两段是否都完整存在？"),
///           ["kind"] = JEVRequest.JEVQuestion.Choice("这段回复的行动一致性属于哪种？", new Dictionary<string, string>
///           {
///               ["ok"] = "说了要做的都做了，或者本轮本来就不需要调用工具",
///               ["said_not_called"] = "【我要】说要调用工具，但实际一个都没调用",
///           }),
///           ["dup"] = JEVRequest.JEVQuestion.Score("重复程度？", "完全新的内容", "意思相近但换了说法", "明显套同一个模板"),
///       });
///   if (r.ok && r.Yes("missing_seg")) { ... }   // r.Yes 是 noul 的布尔判定（默认阈值 0.5）
///
/// 用之前要知道的：
///   · 只喂语义判断（行动一致性、台词重复、要不要重写）。字面格式（段名、括号、字数、markdown）
///     用正则，别喂它 —— 实测拿"【分析】有没有以 】 闭合"考它，4 次全判错，跟正则一致率只有 90%。
///   · 它不产出文本、不支持流式、没有工具调用；context 64k（state + 最长单题 32k），
///     choice 最多 255 项，score 只能 2~10 档。
///   · 延迟：中转 P50 ≈ 300ms、P95 ≈ 1s（模型本体只有 135~165ms，其余是网络），
///     所以调用方要能降级：超时/失败当没这回事，别把 AI 那一轮卡住。
///   · 单次检查约 ¥0.0003。
/// </summary>
public static class JEVRequest
{
    #region 站点 / 模型

    /// <summary>国内中转（默认）。注意 key 的分组要选 JEV。</summary>
    public const string RelayBaseUrl = "https://aiask.me";

    /// <summary>官方站（$0.042/M 更便宜、模型本体更快，但国内网络不一定通）。</summary>
    public const string OfficialBaseUrl = "https://api.typesafe.ai";

    /// <summary>接口根地址，运行时可切换：JEVRequest.BaseUrl = JEVRequest.OfficialBaseUrl;</summary>
    public static string BaseUrl = RelayBaseUrl;

    /// <summary>模型名。实测中转真的返回 jev-1.13.0（传别的名字会被服务端拒，不是万能转发）。</summary>
    public static string Model = "jev-latest";

    public static string Endpoint => BaseUrl.TrimEnd('/') + "/v1/systemone";

    #endregion

    #region key

    /// <summary>key 文件名，放在 Application.persistentDataPath 下（和 DeepSeek 的 Key.txt 分开，各是各的）。</summary>
    public static string KeyFileName = "JevKey.txt";

    public static string KeyPath => Path.Combine(Application.persistentDataPath, KeyFileName);

    private static string cachedKey;

    /// <summary>当前使用的 key（第一次访问时读盘并缓存，不会打进日志）。</summary>
    public static string Key
    {
        get
        {
            if (cachedKey == null) cachedKey = LoadKey();
            return cachedKey;
        }
    }

    public static bool HasKey => !string.IsNullOrEmpty(Key);

    /// <summary>换过 key 文件之后调一下，下次请求重新读盘。</summary>
    public static void ReloadKey() => cachedKey = null;

    private static string LoadKey()
    {
        try
        {
            if (File.Exists(KeyPath)) return File.ReadAllText(KeyPath).Trim();
            Debug.LogError($"[JEVRequest] {KeyFileName} not found at {KeyPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[JEVRequest] 读 key 失败：{e.Message}（{KeyPath}）");
        }
        return "";
    }

    #endregion

    #region 超时 / 计费 / 日志

    /// <summary>
    /// 单次请求超时（秒，0 = 不超时）。UnityWebRequest 默认永不超时，连接卡住时
    /// request.isDone 会一直是 false，等它的那一轮就永久卡死；Jev 实测 P95 ≈ 1s、冷启动 ≈ 630ms，
    /// 给个上界，超时按失败返回。
    /// </summary>
    public static int TimeoutSeconds = 10;

    /// <summary>每百万输入 token 的美元价（输出不计费）：中转 $0.063 / 官方 $0.042。</summary>
    public static double InputPricePerMillion => BaseUrl.Contains("aiask") ? 0.063 : 0.042;

    /// <summary>打开后每次成功请求都会打一行（模型、耗时、token、估算花费）；默认关，免得刷屏。</summary>
    public static bool LogRequests = false;

    #endregion

    #region 题

    /// <summary>一道题。三个字段和服务端 questions 一一对应，字段名必须是小写。</summary>
    public class JEVQuestion
    {
        /// <summary>noul / choice / score。</summary>
        public string type;
        /// <summary>要它判断的问题本身。</summary>
        public string instructions;
        /// <summary>choice 传 Dictionary&lt;string, string&gt;（选项 key → 说明，最多 255 项）；
        /// score 传 string[]（各档说明，只能 2~10 档）；noul 可以不填。</summary>
        public object criteria;

        public static JEVQuestion Noul(string instructions) =>
            new JEVQuestion { type = "noul", instructions = instructions };

        public static JEVQuestion Choice(string instructions, IDictionary<string, string> options) =>
            new JEVQuestion { type = "choice", instructions = instructions, criteria = options };

        public static JEVQuestion Score(string instructions, params string[] levels) =>
            new JEVQuestion { type = "score", instructions = instructions, criteria = levels };
    }

    #endregion

    #region 答

    /// <summary>一次请求的结果。失败不抛异常：ok = false + error，调用方照常往下走。</summary>
    public class JEVResult
    {
        public bool ok;
        public string error = "";
        public long httpCode;
        /// <summary>服务端返回的模型版本，例如 jev-1.13.0。</summary>
        public string model = "";
        public long elapsedMs;
        public int inputTokens;
        public int outputTokens;
        /// <summary>服务端原样返回的 answers（key = 你给的题名）。</summary>
        public JObject answers;

        private JToken Node(string key)
        {
            if (answers == null || string.IsNullOrEmpty(key)) return null;
            return answers[key];
        }

        /// <summary>noul 题的概率（0~1）；拿不到返回 -1。</summary>
        public double Noul(string key) => Node(key)?["noul"]?.Value<double>() ?? -1;

        /// <summary>noul 题的布尔判定。</summary>
        public bool Yes(string key, double threshold = 0.5) => Noul(key) >= threshold;

        /// <summary>choice 题选中的选项 key；拿不到返回空串。</summary>
        public string Choice(string key) => Node(key)?["choice"]?.Value<string>() ?? "";

        /// <summary>choice 题的置信度；拿不到返回 -1。</summary>
        public double Confidence(string key) => Node(key)?["confidence"]?.Value<double>() ?? -1;

        /// <summary>choice 题的全选项概率分布；拿不到返回空表。</summary>
        public Dictionary<string, double> Probabilities(string key)
        {
            var dict = new Dictionary<string, double>();
            if (Node(key)?["probabilities"] is JObject o)
                foreach (var kv in o) dict[kv.Key] = kv.Value.Value<double>();
            return dict;
        }

        /// <summary>score 题的概率加权平均分（小数）；拿不到返回 -1。</summary>
        public double Score(string key) => Node(key)?["score"]?.Value<double>() ?? -1;

        /// <summary>某道题的原始返回，想自己看全就取这个。</summary>
        public JToken Raw(string key) => Node(key);

        /// <summary>这次请求的估算花费（美元）。</summary>
        public double CostUsd => inputTokens / 1e6 * InputPricePerMillion;
    }

    #endregion

    #region 请求

    /// <summary>
    /// 发一次判断请求。state 是素材（字符串 / 匿名对象 / 数组都行），questions 是题（至少一道）。
    /// 网络错误、超时、服务端报错都只返回 ok = false 的结果，不抛异常。
    /// </summary>
    public static async Task<JEVResult> Ask(object state, IDictionary<string, JEVQuestion> questions)
    {
        var result = new JEVResult();

        if (!HasKey)
        {
            result.error = $"没读到 key：{KeyPath}";
            return result;
        }
        if (questions == null || questions.Count == 0)
        {
            result.error = "questions 是空的，至少要有一道题";
            return result;
        }

        var payload = new JObject
        {
            ["model"] = Model,
            ["state"] = state == null ? JValue.CreateNull() : JToken.FromObject(state),
            ["questions"] = JObject.FromObject(questions),
        };

        var sw = Stopwatch.StartNew();
        using (var request = new UnityWebRequest(Endpoint, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Key}");
            if (TimeoutSeconds > 0) request.timeout = TimeoutSeconds;

            request.SendWebRequest();
            while (!request.isDone) await Task.Yield();

            sw.Stop();
            result.elapsedMs = sw.ElapsedMilliseconds;
            result.httpCode = request.responseCode;

            if (request.result != UnityWebRequest.Result.Success)
            {
                result.error = DescribeHttpFailure(request);
                Debug.LogWarning($"[JEVRequest] 判断失败（{result.elapsedMs}ms）：{result.error}");
                return result;
            }

            string body = request.downloadHandler.text;
            try
            {
                var json = JObject.Parse(body);
                result.model = json["model"]?.Value<string>() ?? "";
                result.answers = json["answers"] as JObject;
                result.inputTokens = json["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                result.outputTokens = json["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                result.ok = result.answers != null && result.answers.HasValues;
                if (!result.ok) result.error = "返回里没有 answers：" + Head(body, 300);
            }
            catch (Exception e)
            {
                result.error = $"解析失败：{e.Message}；原文：{Head(body, 300)}";
            }

            if (result.ok)
            {
                if (LogRequests)
                    Debug.Log($"[JEVRequest] {result.model} {result.elapsedMs}ms 输入 {result.inputTokens} / 输出 {result.outputTokens} token ≈ ${result.CostUsd:F6}");
            }
            else
            {
                Debug.LogWarning($"[JEVRequest] {result.error}");
            }

            return result;
        }
    }

    #endregion

    #region 工具

    /// <summary>把 HTTP 失败信息拼成可诊断的字符串：错误、状态码、响应体（服务端的真实原因通常只在 body 里）。</summary>
    private static string DescribeHttpFailure(UnityWebRequest request)
    {
        string body = "";
        try { body = request.downloadHandler != null ? request.downloadHandler.text : ""; } catch { body = ""; }
        return $"JEV Error: {request.error} / HTTP {(int)request.responseCode} / body: {Head(body, 600)}";
    }

    private static string Head(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    #endregion
}
