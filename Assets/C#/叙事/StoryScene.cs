using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 舞台演出的视觉规范（相当于前端的 design token）。
/// 三个场景都从这里取色取尺寸，保证「卡片式」外观统一：
/// 深色圆角面板 + 亮色描边 + 留白，图片与文本一律从屏幕外平移进来。
/// </summary>
public static class StageStyle
{
    // —— 卡片 ——
    /// <summary>卡片底色：无框，只靠底色和背景分层，所以比原来亮一档。</summary>
    public static readonly Color PanelBg = new Color(0.118f, 0.133f, 0.169f, 0.92f);
    /// <summary>描边色。现在 Wall = 0（无框），留着给需要描边的地方。</summary>
    public static readonly Color PanelWall = new Color(0.216f, 0.247f, 0.310f, 1f);

    // —— 文字 ——
    public static readonly Color TextMain = new Color(0.957f, 0.969f, 1f, 1f);
    public static readonly Color TextDim = new Color(0.620f, 0.663f, 0.741f, 1f);

    // —— 思考框（比正文更暗、更冷，明显区分开）——
    public static readonly Color ThinkBg = new Color(0.098f, 0.110f, 0.141f, 0.92f);
    public static readonly Color ThinkWall = new Color(0.259f, 0.302f, 0.384f, 1f);
    /// <summary>思考文字：原来太暗看不清，提亮到接近正文。</summary>
    public static readonly Color ThinkText = new Color(0.62f, 0.667f, 0.757f, 1f);

    // —— 强调色（选中的选项框、✔ 标记、名字下的色条）——
    public static readonly Color Mark = new Color(1f, 0.847f, 0.404f, 1f);

    // —— 字号 ——
    /// <summary>
    /// 3D TextMeshPro 在本工程的换算：**1 点 = 0.1 世界单位**。
    /// 来源：TMP_Text.cs 的 `baseScale = fontSize / faceInfo.pointSize * scale * orthographicMultiplier`，
    /// 这里 faceInfo.pointSize = 90、scale = 1、3D 文本的 orthographicMultiplier = 0.1。
    /// 舞台可视范围约 17.8 × 10 世界单位，所以正文大约 0.26 单位、大标题 0.6 单位。
    /// </summary>
    public const float PointsPerUnit = 10f;

    /// <summary>把「想让这一行占的世界单位高度」换成 TMP 的 fontSize。</summary>
    public static float FontSize(float worldHeight) => worldHeight * PointsPerUnit;

    /// <summary>TMP 富文本的 &lt;size=…&gt; 开标签（参数是世界单位高度，配合 &lt;/size&gt; 用）。</summary>
    public static string SizeTag(float worldHeight) => "<size=" + FontSize(worldHeight).ToString("0.##") + ">";

    // —— 尺寸 ——
    /// <summary>圆角半径（世界单位）。小圆角才像前端卡片，别做成药丸。</summary>
    public const float Corner = 0.16f;
    /// <summary>描边宽度（世界单位）。0 = 无框：卡片只靠底色分层。</summary>
    public const float Wall = 0f;
    /// <summary>正文卡的文字内边距（世界单位）：文字不贴描边。</summary>
    public const float Pad = 0.34f;
    /// <summary>
    /// 正文（AI 的思考 / 对话）的字面膨胀：就是 TMP 材质上的 Dilate（_FaceDilate）。
    /// 负值让笔画细一点、不糊。名字那种要压在立绘上的才用正值。
    /// </summary>
    public const float BodyDilate = -0.12f;

    // —— 节奏（秒）——
    public const float In = 0.2f;      // 入场：慢一点，平移看得清
    public const float Out = 0.2f;     // 出场
    public const float Distance = 10f; // 入场起点距目标点的距离（足够从屏幕外进来）
    public const float Hold = 1.7f;    // 一句话读完的停顿
    public const float Short = 0.5f;   // 短停顿

    /// <summary>
    /// 把一段可能很长的思考裁短。**保留换行**（AI 的思考里本来就有分段），
    /// 只把 \r\n 归一、连续空行压成一个，最后超长才截断加省略号。
    ///
    /// 截断位置取「最后一个句末标点 / 换行」（>= 上限的一半），
    /// 不这么干的话永远是硬切在句子中间，观众看到的最后一句是断的 —— 比截短本身更难读。
    /// 实在找不到句末才退回硬切。
    /// </summary>
    public static string Clamp(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        while (text.Contains("\n\n\n"))
            text = text.Replace("\n\n\n", "\n\n");
        if (max <= 0 || text.Length <= max) return text;

        int cut = LastSentenceEnd(text, max);
        return text.Substring(0, cut).TrimEnd() + "…";
    }

    /// <summary>在 text 的前 max 个字符里找最后一个句末标点 / 换行的位置（找不到就返回 max）。</summary>
    private static int LastSentenceEnd(string text, int max)
    {
        const string stops = "。！？…—；\n";
        int floor = Mathf.Max(1, max / 2);
        for (int i = max - 1; i >= floor; i--)
            if (stops.IndexOf(text[i]) >= 0) return i + 1;
        return max;
    }
}

public abstract class StoryScene
{
    protected static StoryTeller stage => StoryTeller.Instance;

    /// <summary>演出开关关掉时整段跳过。</summary>
    protected static bool Skipped => !StoryTeller.CanPlay;

    public abstract IEnumerator Play();

    #region 建卡片的便捷方法

    /// <summary>建一块文本卡。panel=false 时只有文字，没有可见面板。</summary>
    protected static StoryTeller.Item Card(
        string content, Vector2 pos, Vector2 size,
        Color bg, Color wall, Color textColor,
        bool panel = true, float corner = StageStyle.Corner, float fontSize = 0f,
        TextAlignmentOptions align = TextAlignmentOptions.Center, float textPadding = 0f,
        float faceDilate = 0f, TextOverflowModes overflow = TextOverflowModes.Overflow)
    {
        StoryTeller s = stage;
        if (s == null) return null;
        StoryTeller.Item item = s.CreateText(content, pos, size);
        if (item == null) return null;
        item.containerVisible = panel;
        item.containerColor = bg;
        item.wallColor = wall;
        item.textColor = textColor;
        item.cornerRadius = corner;
        item.wallWidth = StageStyle.Wall;
        item.fontSize = fontSize;
        item.align = align;
        item.textPadding = textPadding;
        item.textFaceDilate = faceDilate;
        item.textOverflow = overflow;
        s.ApplyItem(item);
        return item;
    }

    /// <summary>正文卡片：深色小圆角面板 + 细描边，文字默认左对齐并留内边距（像前端的内容卡）。</summary>
    protected static StoryTeller.Item Panel(string content, Vector2 pos, Vector2 size, float fontSize = 0f,
        TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
        => Card(content, pos, size, StageStyle.PanelBg, StageStyle.PanelWall, StageStyle.TextMain,
                true, StageStyle.Corner, fontSize, align, StageStyle.Pad, StageStyle.BodyDilate,
                TextOverflowModes.Ellipsis);

    /// <summary>只有文字、没有底板的标题 / 标签，居中。</summary>
    protected static StoryTeller.Item Label(string content, Vector2 pos, Vector2 size, Color color, float fontSize = 0f)
        => Card(content, pos, size, StageStyle.PanelBg, StageStyle.PanelWall, color, false, StageStyle.Corner, fontSize);

    /// <summary>思考卡片：**无背景**，只有一段暗一档的文字（和对话的实心卡片分层）。</summary>
    protected static StoryTeller.Item Think(string content, Vector2 pos, Vector2 size, float fontSize = 0f)
        => Card(content, pos, size, StageStyle.ThinkBg, StageStyle.ThinkWall, StageStyle.ThinkText,
                false, StageStyle.Corner, fontSize, TextAlignmentOptions.TopLeft, StageStyle.Pad, StageStyle.BodyDilate,
                TextOverflowModes.Ellipsis);

    /// <summary>场次大标题：一行大字，居中，无底板。</summary>
    protected static StoryTeller.Item Title(string text, float y = 4.4f, float worldHeight = 0.72f)
        => Label(text, new Vector2(0f, y), new Vector2(14f, 1.3f), StageStyle.TextMain, StageStyle.FontSize(worldHeight));

    /// <summary>
    /// 立绘名字：阵营色 + 深色描边（压在立绘上也读得清），排序号在立绘之上。
    /// 名字**不居中**：靠立绘那一侧的外沿对齐（立绘在左就左对齐、在右就右对齐），
    /// 不再压在立绘正下方中间。align 留空时按 pos.x 自动选。
    /// </summary>
    protected static StoryTeller.Item Nameplate(string name, Vector2 pos, Vector2 size, Color accent,
        TextAlignmentOptions? align = null, float worldHeight = 0.72f)
    {
        StoryTeller.Item item = Label(name, pos, size, accent, StageStyle.FontSize(worldHeight));
        if (item != null)
        {
            item.align = align ?? (pos.x < 0f ? TextAlignmentOptions.Left : TextAlignmentOptions.Right);
            item.sortingOrder = 6;                    // 文字排到 8，盖住立绘
            item.textOutlineWidth = 0.22f;            // 描边：立绘同色背景上也能看清
            item.textOutlineColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            item.textFaceDilate = 0.12f;              // 字面稍微膨胀一点，字更实
            stage.ApplyItem(item);
        }
        return item;
    }

    /// <summary>建一块图片卡（Mesh.prefab）。panel=false 时容器透明，只看到图片本体。zoom 用来顶掉原图四周的透明留白。</summary>
    protected static StoryTeller.Item Picture(Sprite sprite, Vector2 pos, Vector2 size, Color tint, bool panel = false, float zoom = 1f)
    {
        StoryTeller s = stage;
        if (s == null) return null;
        StoryTeller.Item item = s.CreatePicture(sprite, pos, size);
        if (item == null) return null;
        item.containerVisible = panel;
        item.containerColor = StageStyle.PanelBg;
        item.wallColor = StageStyle.PanelWall;
        item.textColor = tint;
        item.cornerRadius = StageStyle.Corner;
        item.wallWidth = StageStyle.Wall;
        item.spriteZoom = zoom;
        s.ApplyItem(item);
        return item;
    }

    /// <summary>
    /// 角色在 AIAgent 卡片里配的相对大小（cardset 的 Scale → CharacterCard.Scale，AIAgent.Awake 里拷过去）。
    /// 拿不到就是 1。舞台上立绘的实际大小 = 版面尺寸 × 这个值。
    /// </summary>
    protected static float RelativeSize(int owner)
    {
        AIAgent agent = AIAgent.Instance;
        if (agent == null || owner <= 0) return 1f;
        AIAgent.CharacterCard card = agent.cards.Find(c => c.position == owner);
        return card != null && card.Scale > 0.01f ? card.Scale : 1f;
    }

    /// <summary>
    /// 立绘下沿渐隐用的贝塞尔中间点：(在渐隐区间里的比例, 该点的透明度)。
    /// 区间中点只到 0.22（线性时是 0.5），所以下摆是「越往下越透、慢慢淡入」的非线性渐变，
    /// 不是一条直杠 —— 名字压在立绘重叠处照样看得清。想换别的弯法就在调用处传 fadeBottomMid。
    /// </summary>
    public static readonly Vector2 FadeBottomMid = new Vector2(0.5f, 0.5f);

    /// <summary>
    /// 建一块立绘卡（按阵营取 Resources 里的立绘）。
    /// 尺寸 = 版面尺寸 × 角色自己的相对大小；zoom 再顶掉原图透明边。
    /// fadeBottom &gt; 0 时把**下沿**渐隐到透明（底部 fadeBottom 这一段从全透渐到不透明）——
    /// 立绘放大后下摆会压到名字上，靠它让重叠处透明、名字照样看得清；
    /// 渐变曲线由 fadeBottomMid 这个贝塞尔中间点决定（默认 FadeBottomMid：非线性、慢慢淡入）。
    /// </summary>
    protected static StoryTeller.Item Portrait(int owner, SpriteEmotion emo, Vector2 pos, Vector2 size,
        float zoom = 1.25f, float fadeBottom = 0f, Vector2? fadeBottomMid = null)
    {
        Sprite sprite = StoryTeller.LoadPortrait(owner, emo);
        float raw = RelativeSize(owner);
        Vector2 want = size * raw * PortraitScale;                // 全局放大：三场演出的立绘一起变大
        Vector2 box = FitInStage(sprite, want, ref pos, zoom);   // 只做「尽量别出画」，绝不大幅缩
        StoryTeller.Item item = Picture(sprite, pos, box, Color.white, false, zoom);
        if (item != null && fadeBottom > 0f)
        {
            item.edgeBottom = new Vector4(0, fadeBottom, 0f, 1f);
            item.edgeBottomMid = fadeBottomMid ?? FadeBottomMid;
            stage.ApplyItem(item);
        }

        // 记账：立绘**实际画多大**（观众看到的尺寸）。只有被 FitInStage 收过才打，正常情况不刷屏。
        // 注意别拿「画出来的宽」跟「框的宽」比 —— 立绘是等比贴合的，通常高顶满、宽只有框宽的 5~6 成，那是正常的。
        if (item != null && item.spriteDisplay != null && box.x < want.x * 0.95f)
        {
            Vector2 drawn = item.spriteDisplay.GetDrawnSize(sprite);
            StoryTeller s2 = stage;
        }
        return item;
    }

    /// <summary>
    /// 立绘「尽量别被舞台边缘切掉」：按 sprite **实际画出来**的尺寸算（keepAspect 之后的大小，
    /// 不是那个更大的外框），出画就等比缩小；**下沿永远钉在舞台下沿以内**（多出来的高度往上长），
    /// 所以不管缩没缩，人都「站」在地上。
    ///
    /// 允许出画 FitAllowance 那么多：立绘 PNG 四周本来就是透明留白，严格按整张图的边框卡尺寸，
    /// 人物会显得比可用空间小一圈。想要更大就调大 PortraitScale / FitAllowance 这一对。
    /// 缩放有底线（最多缩到 6 成），舞台尺寸拿到不靠谱的小值时（换分辨率 / 录制重设视口那几帧）一律不缩。
    /// </summary>
    private static Vector2 FitInStage(Sprite sprite, Vector2 size, ref Vector2 pos, float zoom)
    {
        StoryTeller s = stage;
        if (s == null || sprite == null || size.x <= 0f || size.y <= 0f) return size;

        Vector2 half = s.StageSize * 0.5f;
        if (half.x < 4f || half.y < 3f) return size;   // 舞台尺寸不像舞台：一个像素都不缩

        Vector2 frame = size * Mathf.Max(0.01f, zoom);
        Vector2 sp = SceneSpriteDisplay.GetSpriteWorldSize(sprite);
        Vector2 drawn = sp.x > 0f && sp.y > 0f
            ? sp * Mathf.Min(frame.x / sp.x, frame.y / sp.y)
            : frame;

        float availW = Mathf.Max(0.4f, (half.x - Mathf.Abs(pos.x)) * 2f) * FitAllowance;
        float availH = Mathf.Max(0.4f, (half.y - Mathf.Abs(pos.y)) * 2f) * FitAllowance;
        float fit = Mathf.Min(1f, Mathf.Min(availW / Mathf.Max(0.01f, drawn.x), availH / Mathf.Max(0.01f, drawn.y)));
        fit = Mathf.Clamp(fit, 0.6f, 1f);

        float h = drawn.y * fit;                                     // 缩放后的实际高度
        pos.y = Mathf.Max(pos.y - h * 0.5f, -half.y) + h * 0.5f;      // 下沿兜在舞台下沿以内
        return size * fit;
    }

    /// <summary>
    /// 立绘「有没有在说话」：说话的那位保持原样，另一位整体调淡（走网点可见度，越淡网点越稀）。
    /// </summary>
    protected static void PortraitFocus(StoryTeller.Item portrait, bool speaking)
    {
        if (portrait == null) return;
        float v = speaking ? 1f : DimVisibility;
        portrait.spriteVisibility = v;
        if (portrait.spriteDisplay != null)
        {
            portrait.spriteDisplay.visibility = v;
            portrait.spriteDisplay.Rebuild();
        }
    }

    /// <summary>没在说话的立绘的整体可见度（网点口径）。</summary>
    public const float DimVisibility = 0.45f;

    /// <summary>立绘统一放大倍率：三场演出一起变（想整体再大/再小就改这一个数）。</summary>
    public const float PortraitScale = 1.12f;

    /// <summary>立绘允许出画的比例：按整张 PNG 的边框卡尺寸会显得人小一圈，放宽这么多，人物才顶得满。</summary>
    public const float FitAllowance = 1.12f;

    #endregion

    #region 文案组件（小标题 + 正文拼进同一块 TMP，不额外占一个物体）

    /// <summary>思考条固定的小标题：明确告诉观众这是「还没说出口的思考」，不是他说的话。</summary>
    public const string ThinkCaption = "AI 的思考";

    /// <summary>小标题的世界单位高度。</summary>
    protected const float CaptionH = 0.32f;
    /// <summary>正文的世界单位高度。</summary>
    protected const float BodyH = 0.36f;
    /// <summary>思考条小标题的颜色：冷蓝，和正文、和对话标题都区分开。</summary>
    protected const string ThinkCaptionColor = "#88B8FF";
    /// <summary>对话卡小标题的颜色：亮中性（比正文暗一点点）。</summary>
    protected const string CaptionColor = "#C9D4E6";



    /// <summary>
    /// 把文本里出现的角色名自动染成对应阵营色 —— 和中央列表 / 炮塔飘字 / 顶部横幅用的是同一个
    /// AIAgent.ColorizeAINames（顺带拦掉 [emo:xxx]）。舞台上的思考与对话都走它。
    /// </summary>
    protected static string Colorize(string text)
        => string.IsNullOrEmpty(text) ? text : AIAgent.ColorizeAINames(text);

    /// <summary>思考条小标题：谁的思考就写谁的名字（名字会自动染成阵营色），拿不到名字才写「AI 的思考」。</summary>
    protected static string ThinkCaptionOf(string owner)
        => string.IsNullOrWhiteSpace(owner) ? ThinkCaption : owner.Trim() + " 的思考";

    /// <summary>
    /// 思考条正文：先一行小标题（带是谁在思考），再内容。拿不到思考过程时给一句说明，而不是留一块空白。
    /// 三场演出都用它，保证「先出思考、再开口」的观感一致。
    /// 字号必须走 StageStyle.SizeTag（参数是世界单位高度）——TMP 的绝对 &lt;size&gt; 是点数，1 点 = 0.1 世界单位。
    ///
    /// max 是**兜底**上限，不是常规裁切：思考条的框在升级那场是 13 × 3.8 世界单位、字号 0.3（≈33px，行高约 1.2 倍），
    /// 去掉内边距后大约能放 10 行 × 30 字，所以 320 字以内基本都装得下。
    /// 原来这里是 140，正常两段思考都会被平白砍掉一截（就是「平白无故多个省略号」的来源）。
    /// </summary>
    protected static string ThinkBody(string thinking, string owner = null, int max = 320)
    {
        string head = ThinkHead(owner);
        if (string.IsNullOrWhiteSpace(thinking))
            return head + StageStyle.SizeTag(BodyH) + "<color=" + CaptionColor + ">（这一轮没有留下思考过程）</color></size>";
        return head + StageStyle.SizeTag(BodyH) + "<color=#9EABBF>" + Colorize(StageStyle.Clamp(thinking, max)) + "</color></size>";
    }

    /// <summary>思考条的小标题那一行（名字走阵营色，跟在后面的「的思考」保持冷蓝）。</summary>
    private static string ThinkHead(string owner)
        => StageStyle.SizeTag(CaptionH) + "<color=" + ThinkCaptionColor + ">" + Colorize(ThinkCaptionOf(owner)) + "</color></size>\n";

    /// <summary>思考条的等待态：思考还没回来时显示它，位置和正式思考内容完全一致，换字不会跳。</summary>
    protected static string ThinkWaitingFor(string owner = null)
        => ThinkHead(owner)
         + StageStyle.SizeTag(BodyH) + "<color=" + CaptionColor + ">思考中…</color></size>";

    /// <summary>思考条的等待态（不带名字的旧写法）。</summary>
    protected static string ThinkWaiting => ThinkWaitingFor(null);

    /// <summary>给一块面板加一行小标题（悄悄话 / 回答 / 狠话…），小标题比正文暗一档。bodyH 是世界单位高度。</summary>
    protected static string Titled(string caption, string body, float bodyH = BodyH)
        => StageStyle.SizeTag(CaptionH) + "<color=" + CaptionColor + ">" + Colorize(caption) + "</color></size>\n"
         + StageStyle.SizeTag(bodyH) + "<color=#E9EEF7>" + Colorize(body) + "</color></size>";

    /// <summary>名字下面那条阵营色短线（前端里常见的 accent underline）。</summary>
    protected static StoryTeller.Item AccentBar(Color color, Vector2 pos, float width = 1.5f, float height = 0.09f)
    {
        StoryTeller.Item bar = Card("", pos, new Vector2(width, height), color, color, color,
            true, height * 0.5f, 1f);
        return bar;
    }

    #endregion

    #region 常用动作

    /// <summary>改一块文本卡的内容并重新播逐字入场。</summary>
    protected static void SetText(StoryTeller.Item item, string content, bool replay = true)
    {
        if (item == null || item.textDisplay == null) return;
        item.str1 = content;
        item.textDisplay.content = content;
        item.textDisplay.ApplyContent();
        if (replay) item.textDisplay.PlayIn();
    }

    /// <summary>不播逐字动画，直接整段显示。</summary>
    protected static void SetTextHard(StoryTeller.Item item, string content)
    {
        if (item == null || item.textDisplay == null) return;
        item.str1 = content;
        item.textDisplay.content = content;
        item.textDisplay.ApplyContent();
        item.textDisplay.StopIn();
    }

    /// <summary>选中一项：亮起来，其余的压暗。</summary>
    protected static void Highlight(StoryTeller.Item chosen, IEnumerable<StoryTeller.Item> others, Color accent)
    {
        StoryTeller s = stage;
        if (s == null) return;
        if (chosen != null) s.SetHighlight(chosen, true, accent);
        if (others == null) return;
        foreach (StoryTeller.Item item in others)
        {
            if (item == null || item == chosen) continue;
            s.SetDimmed(item, true);
        }
    }

    /// <summary>一批卡片同时平移入场（各自用自己的 enterFrom / enterDistance / enterDuration）。</summary>
    protected static IEnumerator SlideInAll(StoryTeller s, IEnumerable<StoryTeller.Item> items)
    {
        if (s == null || items == null) yield break;
        float longest = 0f;
        int count = 0;
        foreach (StoryTeller.Item it in items)
        {
            if (it == null) continue;
            s.StartCoroutine(s.SlideIn(it, it.enterFrom, it.enterDistance, it.enterDuration));
            if (it.enterDuration > longest) longest = it.enterDuration;
            count++;
        }
        if (count == 0) yield break;
        yield return s.WaitStage(longest);
    }

    /// <summary>一批卡片同时平移出场。</summary>
    protected static IEnumerator SlideOutAll(StoryTeller s, IEnumerable<StoryTeller.Item> items, StoryTeller.Direction to, float distance)
    {
        if (s == null || items == null) yield break;
        int count = 0;
        foreach (StoryTeller.Item it in items)
        {
            if (it == null) continue;
            s.StartCoroutine(s.SlideOut(it, to, distance, StageStyle.Out));
            count++;
        }
        if (count == 0) yield break;
        yield return s.WaitStage(StageStyle.Out);
    }

    /// <summary>给一组卡片统一设入场方向 / 距离 / 时长。</summary>
    protected static void SetupEnter(IEnumerable<StoryTeller.Item> items, StoryTeller.Direction from, float distance, float duration)
    {
        if (items == null) return;
        foreach (StoryTeller.Item it in items)
        {
            if (it == null) continue;
            it.enterFrom = from;
            it.enterDistance = distance;
            it.enterDuration = duration;
        }
    }

    /// <summary>
    /// 等一块文本的逐字入场**真的打完**（按舞台时钟轮询 FieldArrivalModifier.IsPlaying），
    /// 打完就返回，中间不留空档 —— 上一块字一停，下一块内容立刻开始。
    ///
    /// 不要再用 ReadTime 估时长来等打字：PlayIn 会把 charInterval 夹在 0.02~0.1 之间自动加速，
    /// 估算值往往比实际打字长好几秒，那段差值就是「上一段早打完了、下一段还不出来」的死时间。
    /// extra 是打完之后额外停留的秒数，默认 0（下一块内容播放时这一块还在台上，观众可以继续读）。
    /// </summary>
    protected static IEnumerator WaitTextIn(StoryTeller s, StoryTeller.Item item, float extra = 0f)
    {
        if (s == null) yield break;
        FieldArrivalModifier anim = item != null && item.textDisplay != null ? item.textDisplay.Anim : null;
        if (anim != null)
        {
            float guard = 0f;   // 兜底：万一动画推进不动（组件被停掉之类），别把整场演出吊死
            while (anim.IsPlaying && guard < 30f)
            {
                yield return s.WaitStage(s.ClockDelta);
                guard += s.ClockDelta;
            }
        }
        if (extra > 0f) yield return s.WaitStage(extra);
    }

    /// <summary>
    /// 按字数估一个「读完」时长：Text.prefab 的逐字参数是 charDuration 0.5 / charInterval 0.1，
    /// 所以一段字至少要 0.5 + 0.1×(字数-1) 秒才播完，之后再留 hold 秒让人看清。
    /// 下限抬到 1.8 秒、上限默认 7 秒：刚出来就切走太快，长思考也不能无限拖。
    /// 想让某一场停留短一点就传自己的 hold / max（开场那场就用小一档）。
    /// </summary>
    protected static float ReadTime(string text, float hold = 1.2f, float max = 7f)
    {
        int len = string.IsNullOrEmpty(text) ? 0 : text.Length;
        return Mathf.Clamp(0.9f + 0.14f * Mathf.Max(0, len - 1) + hold, 1.8f, max);
    }

    #endregion
}
