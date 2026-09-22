using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
// System.Diagnostics 里也有 Debug/Stopwatch：Debug 明确指向 Unity 的（不然 CS0104 不明确引用）
using Debug = UnityEngine.Debug;

/// <summary>
/// 台词拟人化：AI 写出来的【我说】再交给一个更便宜的模型过一遍，
/// 让它贴着这张角色卡的设定、像人说的话，然后拿优化版去显示 / 写进历史 / 落盘。
///
/// **当前走 DeepSeek 官方 API 的 deepseek-flash**（2026-09-22 用户："用Deepseek自己的官方API用deepseek-flash来优化"）。
/// 换之前用的是硅基流动上的 deepseek-ai/DeepSeek-V3.2，同一条台词实测：
///   硅基 V3.2：n=8 平均 10.35s、中位 7.47s、最慢 26.18s（>20s 会撞上 TimeoutSeconds 被掐掉、这句退回原句）；
///   官方 flash：n=18 平均 0.67s、中位 0.68s、最慢 0.97s（4 家并发也没有排队惩罚）；
///   两个供应商的"凭空补人名"发生率一样（8 条样本各 2 条）—— 质量口径没变，只是快了一个数量级。
///   （当时是用人名护栏把这几句退回原句；**护栏已按用户要求撤掉**，见 PolishAsync 里的说明。）
/// 想切回硅基流动：把 ApiUrl / Model / KeyFileName 换成
///   https://api.siliconflow.cn/v1/chat/completions ・ deepseek-ai/DeepSeek-V3.2 ・ SiliconKey.txt，
/// 并且把下面 payload 里的 thinking 字段改回 enable_thinking（硅基用那个名字）。
///
/// 口径（用户定的）：
///   · 系统提示 = 角色卡的 oc + 优化器指令；用户提示 = 【我要】+【我说】。
///   · 其他 AI 的人名、游戏术语必须保留；术语可以用别的、更符合角色的动词去修饰。
///   · [emo:xxx] 是系统自己的表情记号：发出去之前剔掉，拿到结果再贴回末尾（不让模型碰它）。
///   · 只有 MapConfig.humanizeSpeech 打开、且读得到 key 时才发请求；
///     超时 / 网络错 / 返回为空 → 一律原样退回原句，绝不让台词消失、也绝不把回合卡死。
///   · 日志一律带时间戳（[拟人化 HH:mm:ss.fff]），方便和录像、耗时统计对时间。
/// </summary>
public static class SpeechPolisher
{
    #region 站点 / 模型 / key

    /// <summary>DeepSeek 官方对话补全接口（和主回合、行为检查用的是同一个站点）。</summary>
    public static string ApiUrl = "https://api.deepseek.com/v1/chat/completions";

    /// <summary>官方模型名（和主回合、行为检查同一个：deepseek-flash）。</summary>
    public static string Model = "deepseek-flash";

    /// <summary>key 文件名，放在 Application.persistentDataPath 下 —— 现在**和主 API 共用同一把** key（Key.txt）。</summary>
    public static string KeyFileName = "Key.txt";

    public static string KeyPath => Path.Combine(Application.persistentDataPath, KeyFileName);

    private static string cachedKey;

    /// <summary>当前 key（第一次访问读盘并缓存，不打进日志）。</summary>
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
            return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim() : null;
        }
        catch (Exception e)
        {
            Debug.LogError($"{Stamp()} 读 key 失败（{KeyPath}）：{e.Message}");
            return null;
        }
    }

    #endregion

    #region 参数

    /// <summary>单次请求超时（秒）。用户要的是「等」，但等不到得有个底线，超了就退回原句。</summary>
    public static int TimeoutSeconds = 20;

    /// <summary>输出上限：只优化一句台词，给足余量就行。</summary>
    public static int MaxTokens = 300;

    public static float Temperature = 1.1f;

    /// <summary>
    /// 关掉思考（官方 API 的 thinking:{type:disabled}）。**默认 false**：这一层只要把一句台词改得像人说的，
    /// 不需要思维链 —— 开着的时候一句要等 2.7~12s，关掉就回到一秒上下（官方 flash 实测 0.67s 均值）。
    /// 想留一点思考就打开它，并用 ThinkingBudget 压住长度。
    /// </summary>
    public static bool EnableThinking = false;

    /// <summary>
    /// 开着思考时的思维链预算（token）—— **这是硅基流动的字段（thinking_budget），官方 API 没有它**，
    /// 所以现在不再发送；留着只为将来切回硅基时用（官方那边靠 thinking:{type} 开关，不需要预算）。
    /// </summary>
    public static int ThinkingBudget = 128;

    /// <summary>每次调用打一行「原句 → 优化句」（带时间戳）。</summary>
    public static bool LogRequests = true;

    private static bool warnedNoKey;

    #endregion

    #region 文本处理

    private static readonly Regex EmoTagRegex = new Regex(@"\[emo:[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex ThinkRegex = new Regex(@"<think[\s\S]*?<｜end▁of▁thinking｜>", RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex SayPrefixRegex = new Regex(@"^\s*(【?\s*我说\s*】?\s*[:：]?)", RegexOptions.Compiled);

    /// <summary>
    /// 把 content 里【我说：…】那一段的内容换成 newSay（全角/半角冒号都认），其余一个字不动。
    /// 找不到这一段就返回 null（调用方据此什么都不改）。
    /// </summary>
    public static string ReplaceSaySegment(string content, string newSay)
    {
        if (string.IsNullOrEmpty(content) || newSay == null) return null;

        int head = content.IndexOf("【我说：", StringComparison.Ordinal);
        if (head < 0) head = content.IndexOf("【我说:", StringComparison.Ordinal);
        if (head < 0) return null;

        int bodyStart = head + 4;                 // 跳过【我说：
        int end = content.IndexOf('】', bodyStart);
        if (end < 0) return null;

        return content.Substring(0, bodyStart) + newSay + content.Substring(end);
    }

    /// <summary>把模型返回的东西洗成「一句台词」：去思考块、去引号、去【】、折掉换行。</summary>
    private static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string s = ThinkRegex.Replace(raw, "");
        s = s.Replace("\r", " ").Replace("\n", " ");
        s = s.Trim().Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', ' ');
        s = s.Replace("【", "").Replace("】", "");   // 括号会破坏三段格式的解析，直接拿掉
        s = SayPrefixRegex.Replace(s, "");          // 模型偶尔自己带一个「我说：」前缀
        s = SpaceRegex.Replace(s, " ").Trim();
        return s;
    }

    /// <summary>
    /// 每个角色的「消息头」（system 那一条）只拼一次，之后整段复用。
    /// 两个好处：
    ///   ① 内容逐字节稳定 → 平台的前缀缓存（prompt caching）能命中，那段 oc + 规则的输入按缓存价算、首字也更快；
    ///   ② 不再每次调用都重拼一遍（一次请求省几微秒，但主要是为了让缓存一定命中）。
    /// key 用 oc 原文参与，所以改了角色卡自然就是新的一条，不会拿错别人的头；
    /// **会晤的对方名字（counterpartName）也要进 key** —— 不然跟不同人谈的两套头会互相串。
    /// </summary>
    private static readonly Dictionary<string, string> headerCache = new();

    public static string HeaderFor(string name, string oc, string otherNames, PolishMode mode = PolishMode.Public, string counterpartName = null)
    {
        string tag = mode == PolishMode.Whisper ? "W" : (mode == PolishMode.LastWords ? "L" : "P");
        string key = (name ?? "") + "\u0001" + (otherNames ?? "") + "\u0001" + tag + "\u0001" + (oc ?? "") + "\u0001" + (counterpartName ?? "");
        if (headerCache.TryGetValue(key, out string cached)) return cached;

        string built = BuildSystem(name, oc, otherNames, mode, counterpartName);
        headerCache[key] = built;
        if (LogRequests)
            Debug.Log($"{Stamp()} {name} 消息头已缓存（{ModeName(mode)}{(string.IsNullOrWhiteSpace(counterpartName) ? "" : "·对" + counterpartName)} {built.Length} 字，之后每次都原样复用，方便平台前缀缓存命中）");
        return built;
    }

    private static string ModeName(PolishMode mode)
        => mode == PolishMode.Whisper ? "悄悄话" : (mode == PolishMode.LastWords ? "遗言" : "公开台词");

    /// <summary>清掉消息头缓存（换了 oc / 想重新观察缓存是否命中时用）。</summary>
    public static void ClearHeaderCache() => headerCache.Clear();

    /// <summary>每个角色各算各的拟人化计数（这是它的第几次调用），实现 MapConfig.humanizeInterval 的「1010」间隔。</summary>
    private static readonly Dictionary<string, int> polishTicks = new();

    private static int NextPolishTick(string name)
    {
        string key = name ?? "";
        int next = (polishTicks.TryGetValue(key, out int count) ? count : 0) + 1;
        polishTicks[key] = next;
        return next;
    }

    /// <summary>把间隔计数清零（想重新从「这一次做优化」开始数时用）。</summary>
    public static void ResetPolishTicks() => polishTicks.Clear();

    /// <summary>优化器要处理的是哪一种文本 —— 三种走三套 system 规则（各自缓存）。</summary>
    public enum PolishMode
    {
        /// <summary>当众说出口的【我说】：短句、15 字以内。</summary>
        Public,
        /// <summary>秘密会晤里的私下谈判：不许压缩、不许丢信息。</summary>
        Whisper,
        /// <summary>遗言：被击杀前的最后一句话，按原文长度与内容保留，只改语气。</summary>
        LastWords
    }

    /// <summary>
    /// 优化器的系统提示：角色卡 oc + 本项目的硬规则。
    /// **三套分开**（<see cref="PolishMode"/>）：当众台词 / 悄悄话谈判 / 遗言。
    ///
    /// 2026-09-22 按实测改写过一次（用户："改写更保守是不是提示词还能优化一下"）：
    /// 换到官方 deepseek-flash 之后发现它**太保守**（原句照抄/只删两个字）。10 条真实台词 ×4 套写法实测：
    ///   旧版（"文本优化器"，没说要改写）：改写幅度 34%、原样返回 0/10、凭空补人名 0、丢信息 0、0.68s
    ///   新版（"语气改写器" + **必须改写、不许原样返回**）：**49%**、0/10、**0**、0、0.65s ← 采用
    ///   再加两条示例：52% 但补人名 1/10；再抬 temperature 到 1.4：62% 但补人名 2/10（多补一次人名就多一句被退回/出错，不划算）
    /// 所以只换了措辞口径，不加示例、不动 temperature（Temperature 仍 1.1）。
    /// 后来又按用户要求：(a) **撤掉人名护栏**（只靠提示词）、(b) 会晤那一档额外提供对方名字。
    /// 脚本：DSH草稿区\polish-prompt-ab.mjs、polish-live-verify.mjs
    /// </summary>
    private static string BuildSystem(string name, string oc, string otherNames, PolishMode mode, string counterpartName = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(oc)) sb.Append(oc.Trim()).Append("\n\n");

        sb.Append("你是这个角色的台词语气改写器。把给你的那句台词，用这个角色的口吻**重写一遍**，直接输出改写结果，不要任何解释、不要客套话。\n\n");
        sb.Append($"1. 你叫「{name}」，要用你的口癖、语气词、称呼习惯来说这句话。**只许用你自己设定里的口癖，不要借用别的角色的口癖或称呼。**\n");
        if (!string.IsNullOrWhiteSpace(otherNames))
            sb.Append($"2. 场上其他人的名字是：{otherNames}。\n");
        sb.Append("3. **必须改写，不许原样返回**：把原句照抄回来（或只删一两个字）算没完成 —— 换一种说法、换语序、换用词，让人一听就是这个角色在说话。\n");
        sb.Append("4. **情报一个字都不许变**：原句里的数字、坐标、人名、承诺、条件、问句全部保留原样；**不许新增**原句没有的人名、对象、坐标、数字、承诺。\n");
        sb.Append("5. **游戏术语必须保留原词**；**任何人名与称呼都必须照抄原句**：原句没点名的，不许补名字、也不许换成别人的名字；原句点了名的，不许换人、不许改称呼。场上其他人的名字只能出现在原文自己就提到的地方。**说话用第一人称自称（按你自己的角色设定来），不许把自己的名字写进台词里**（写进去会被整句退回）。\n");
        sb.Append("6. 只输出改写后的那一句话本身：不要（解释、引号、括号、换行、动作描写、旁白），不要说「好的」这类客套话。\n");

        if (mode == PolishMode.Whisper)
        {
            // 秘密会晤：这是私下谈判，信息量就是它的价值 —— 绝对不能按短句来处理
            sb.Append("7. 这是私下谈判的一段话：信息密度就是它的价值，**长度按原文来（可以一样长甚至更长），不许压缩、不许丢任何条件或数字**。\n");
            sb.Append("8. 你改的是说法和语气，不是内容；每一句都要换成你的说法，但每一条信息都得在。\n");
            // 用户 2026-09-22："只是在会晤的时候额外提供对方名字" ——
            // 拟人化最容易犯的错就是把交谈对象写成在场别的人（"AI 认错人"），
            // 会晤是一对一的，这里直接把对面是谁告诉模型，从源头堵住。
            if (!string.IsNullOrWhiteSpace(counterpartName))
                sb.Append($"9. 这一场是**私下交谈，对面是「{counterpartName}」**：这段话就是说给他听的 —— 要提对象就提他，**不许写成场上别的人**。\n");
        }
        else if (mode == PolishMode.LastWords)
        {
            // 遗言：短句、但信息（谁杀的、什么心情）不能丢，语气要虚弱
            sb.Append("7. 这是这个角色被击杀前说出口的最后一句话：按**原文长度**处理，原文里提到的对手、武器、事由一个都不许删；" +
                      "语气可以是虚弱、断断续续、不甘、疼痛或自嘲，符合角色设定即可。\n");
            sb.Append("8. 不要加旁白、动作描写或补一句新台词；**不要为了凑短句把信息压掉**，也不要原样抄回来。\n");
        }
        else
        {
            sb.Append("7. 这是**当众说出口**的一句台词：长度跟原句差不多（不要明显变长，也别缩成冷淡的一句话）；口癖、语气词、称呼该加就加，但不许多说新信息。");
        }
        return sb.ToString();
    }

    private static string BuildUser(string want, string say)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(want))
            sb.Append("【我要】").Append(want.Trim()).Append('\n');
        sb.Append("【我说】").Append(say.Trim());
        return sb.ToString();
    }

    #endregion

    #region 请求

    /// <summary>
    /// 把一句台词过一遍拟人化模型（现在 = 官方 deepseek-flash），返回改写后的文本（[emo:xxx] 已按原样贴回）。
    /// name = 说话人名字，oc = 角色卡设定，want = 这一轮的【我要】，say = 【我说】原文，otherNames = 场上其他人的名字。
    /// counterpartName = **秘密会晤的对方名字**（只有 mode = Whisper 时用；2026-09-22 用户："只是在会晤的时候额外提供对方名字"）：
    /// 会晤是一对一的，把对面是谁告诉模型，它才不会把交谈对象写成在场别的人。
    /// 开关关掉 / 没 key / 失败 / 超时 / 返回空 → 原样返回 say。
    /// </summary>
    public static async Task<string> PolishAsync(string name, string oc, string want, string say, string otherNames, PolishMode mode = PolishMode.Public, string counterpartName = null)
    {
        string original = say ?? "";
        if (string.IsNullOrWhiteSpace(original)) return original;

        MapConfig cfg = MapConfig.Instance;
        if (cfg == null || !cfg.humanizeSpeech) return original;

        if (!HasKey)
        {
            if (!warnedNoKey)
            {
                warnedNoKey = true;
                Debug.LogWarning($"{Stamp()} 没读到 key（{KeyPath}），拟人化整局跳过，直接用原句");
            }
            return original;
        }

        // 间隔（MapConfig.humanizeInterval）：每 N 次真正做 1 次 —— 2 就是「1010」，优化一次、跳过一次。
        // 计数**按角色各算各的**（同一个角色的台词 1-0-1-0 交替）；跳过的那次直接返回原句，不产生任何等待。
        // **悄悄话会晤例外：会晤里的每一条「说」全都优化**（谈判的信息量就是它的价值，不能一半生一半熟），
        // 而且它不参与计数 —— 间隔只数公开台词与遗言，公开那边的 1010 不会被会晤插队打乱。
        int interval = cfg.humanizeInterval;
        if (interval <= 0) return original;                       // 0/负数 = 整层不做（会晤也不做）
        if (interval > 1 && mode != PolishMode.Whisper)
        {
            int tick = NextPolishTick(name);
            if ((tick - 1) % interval != 0)
            {
                if (LogRequests)
                    Debug.Log($"{Stamp()} {name} 拟人化按间隔跳过（该角色第 {tick} 次，间隔 {interval}）→ 直接用原句：{original}");
                return original;
            }
        }

        // [emo:xxx] 是我们自己的表情记号：剔掉再发，回来贴回末尾
        Match emo = EmoTagRegex.Match(original);
        string emoTag = emo.Success ? emo.Value : "";
        string plain = EmoTagRegex.Replace(original, "").Trim();
        if (string.IsNullOrWhiteSpace(plain)) return original;

        var payload = new JObject
        {
            ["model"] = Model,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = HeaderFor(name, oc, otherNames, mode, counterpartName) },
                new JObject { ["role"] = "user", ["content"] = BuildUser(want, plain) },
            },
            ["temperature"] = Temperature,
            ["max_tokens"] = MaxTokens,
            ["stream"] = false,
            // 官方 API 的思考开关是 thinking:{type:...}（主回合那边也是这个字段）；
            // 硅基流动用的是 enable_thinking:false —— 换回硅基时这里要跟着换。
            ["thinking"] = new JObject { ["type"] = EnableThinking ? "enabled" : "disabled" },
        };
        // 注意：ThinkingBudget（thinking_budget 字段）是硅基流动的，官方 API 没有这个参数，不再发送。

        var sw = Stopwatch.StartNew();
        try
        {
            using (var request = new UnityWebRequest(ApiUrl, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {Key}");
                if (TimeoutSeconds > 0) request.timeout = TimeoutSeconds;

                request.SendWebRequest();
                while (!request.isDone) await Task.Yield();

                sw.Stop();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"{Stamp()} {name} 拟人化失败（{sw.ElapsedMilliseconds}ms）：{Describe(request)}；这一句用原句");
                    return original;
                }

                string body = request.downloadHandler != null ? request.downloadHandler.text : "";
                string content;
                string usage;
                try
                {
                    var json = JObject.Parse(body);
                    content = json["choices"]?[0]?["message"]?["content"]?.Value<string>();
                    var u = json["usage"];
                    int inTok = u?["prompt_tokens"]?.Value<int>() ?? 0;
                    int outTok = u?["completion_tokens"]?.Value<int>() ?? 0;
                    // 前缀缓存命中多少：命中越多说明「消息头复用」生效（不同服务端的字段名不一样，都试一下）
                    int cachedTok = u?["prompt_cache_hit_tokens"]?.Value<int>()
                                    ?? u?["prompt_tokens_details"]?["cached_tokens"]?.Value<int>()
                                    ?? 0;
                    usage = $"输入 {inTok} / 输出 {outTok} token" + (cachedTok > 0 ? $"（缓存命中 {cachedTok}）" : "（缓存未命中）");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"{Stamp()} {name} 拟人化返回解析失败（{sw.ElapsedMilliseconds}ms）：{e.Message}；原文：{Head(body, 300)}");
                    return original;
                }

                string polished = Sanitize(content);
                if (string.IsNullOrWhiteSpace(polished))
                {
                    Debug.LogWarning($"{Stamp()} {name} 拟人化返回是空的（{sw.ElapsedMilliseconds}ms）；这一句用原句。原文：{Head(body, 300)}");
                    return original;
                }
                // 人名护栏**已按用户要求撤掉**（2026-09-22 用户："去掉，只靠提示词。只是在会晤的时候额外提供对方名字。"）。
                // 撤掉之前这里是 FirstInventedName：改写版里出现原句没有的人名就整句退回原句。
                // 现在改成两条"从源头管"的做法：
                //   ① 提示词里把"任何人名与称呼照抄原句""不许把自己名字写进台词""只用自己的口癖"讲死；
                //   ② 秘密会晤那一档把**对方名字**一起给模型（见 PolishAsync 的 counterpartName），
                //      它知道这一段是说给谁听的，就不会把对象写成在场别的人。
                // 代价（已知并接受）：提示词管不住时，屏幕上可能出现"叫错人"的台词 —— 那属于模型侧的问题，
                // 不再由代码替它兜；实在要收紧就改提示词或把这一档关掉（MapConfig.humanizeSpeech）。
                if (!string.IsNullOrEmpty(emoTag)) polished += " " + emoTag;

                if (LogRequests)
                    Debug.Log($"{name} {sw.ElapsedMilliseconds}ms {usage}「{plain}」→「{polished}」");

                return polished;
            }
        }
        catch (Exception e)
        {
            sw.Stop();
            Debug.LogWarning($"{name} 拟人化异常（{sw.ElapsedMilliseconds}ms）：{e.Message}；这一句用原句");
            return original;
        }
    }

    #endregion

    #region 工具

    /// <summary>日志前缀：统一带时间戳。</summary>
    private static string Stamp() => $"[拟人化 {DateTime.Now:HH:mm:ss.fff}]";

    private static string Describe(UnityWebRequest request)
    {
        string body = "";
        try { body = request.downloadHandler != null ? request.downloadHandler.text : ""; } catch { body = ""; }
        return $"{request.error} / HTTP {(int)request.responseCode} / body: {Head(body, 600)}";
    }

    private static string Head(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    #endregion
}
