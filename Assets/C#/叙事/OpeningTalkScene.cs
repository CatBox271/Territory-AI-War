using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 【开场放狠话】
///
/// 开局第一轮各阵营的赛前宣言。原来的做法是四座炮塔同时飘字，互相盖住；
/// 这里改成一个人一个人上台，版面按前端的思路排：
///   正中一行大标题 → 左侧一张大立绘（下沿压一块阵营色名牌）
///   → 右栏两张卡：上面是「AI 的人设」（读角色卡的 oc），下面是「狠话」（左对齐、带内边距的正文卡）
///   → 两张卡同时弹出，整段在台上停满 3 秒（滑入 + 停留 + 滑出）再一起滑下去，换下一位。
/// </summary>
public class OpeningTalkScene : StoryScene
{
    /// <summary>一个人的一轮上台。</summary>
    public class Line
    {
        public int owner;
        /// <summary>这个人的人设（角色卡的 oc）。原来这里放的是「思考」，开场介绍改成亮人设。</summary>
        public string persona = "";
        public string speech = "";
    }

    // —— 版面（舞台可见范围约 17.8 × 10，中心为原点）——
    private const float FaceX = -6.5f;
    private const float FaceY = -1f;
    private static readonly Vector2 FaceSize = new Vector2(5.2f, 6.5f);
    private const float FaceZoom = 1.18f;          // 立绘原图上下各留一点，靠 zoom 顶满
    private const float FaceFadeBottom = 0.35f;    // 下沿渐隐比例：下摆放透明，压到名字上也看得清

    private const float TextX = 2.1f;              // 右栏中心：右侧留出约 1.1 单位边距
    private const float TextW = 11.4f;
    private const float ThinkY = 0.55f;
    private const float SayY = -3.5f;

    /// <summary>每位角色在台上的整段时长（秒）：滑入 + 停留 + 滑出。</summary>
    private const float PerSpeakerSeconds = 3f;
    /// <summary>从整段时长里扣掉滑入滑出，剩下的才是停在台上的时间（3 − 0.2 − 0.2 = 2.6）。</summary>
    private const float LingerSeconds = PerSpeakerSeconds - StageStyle.In - StageStyle.Out;

    private readonly List<Line> lines;

    public OpeningTalkScene(List<Line> lines)
    {
        this.lines = lines;
    }

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null || lines == null || lines.Count == 0) yield break;

        StoryTeller.Item title = Title("角 色 介 绍", 4.05f, 0.95f);
        yield return s.SlideIn(title, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        yield return s.WaitStage(StageStyle.Short);

        for (int i = 0; i < lines.Count; i++)
        {
            Line line = lines[i];
            if (line == null) continue;
            yield return OneSpeaker(s, line, i + 1, lines.Count);
        }

        yield return s.SlideOut(title, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.Out);
    }

    private IEnumerator OneSpeaker(StoryTeller s, Line line, int index, int total)
    {
        Color accent = MapConfig.Instance != null
            ? MapConfig.Instance.GetColor(line.owner, MapConfig.ColorStage.Towel)
            : Color.white;
        string who = AIAgent.GetStageName(line.owner);

        // 立绘放哪边 = 这座塔在战场上实际在哪边（左/右/右/左那种），整块版面跟着镜像
        float side = PortraitSide(line.owner);           // +1：立绘在左、文字在右；-1：反过来
        StoryTeller.Direction faceFrom = side > 0f ? StoryTeller.Direction.Left : StoryTeller.Direction.Right;
        StoryTeller.Direction textFrom = side > 0f ? StoryTeller.Direction.Right : StoryTeller.Direction.Left;

        // 角落点数：跟着立绘走，永远待在文字栏那一侧的对角
        StoryTeller.Item counter = Label(index + " / " + total, new Vector2(7.9f * side, 4.42f), new Vector2(1.8f, 0.5f),
            StageStyle.TextDim, StageStyle.FontSize(0.24f));

        // 立绘那一侧：大立绘 + 下沿的阵营色名牌
        StoryTeller.Item face = Portrait(line.owner, SpriteEmotion.origin, new Vector2(FaceX * side, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item plate = Nameplate(who, new Vector2(FaceX * side, -4.5f), new Vector2(3.6f, 0.9f), accent);

        // 文字那一侧：人设（上）+ 对话（下）。两张卡同时弹出，内容在弹出前就整段写好（不逐字打）
        StoryTeller.Item persona = Think("", new Vector2(TextX * side, ThinkY), new Vector2(TextW, 5f), StageStyle.FontSize(BodyH));
        StoryTeller.Item say = Panel("", new Vector2(TextX * side, SayY), new Vector2(TextW, 2.6f), StageStyle.FontSize(0.4f));
        SetTextHard(persona, PersonaBody(line.persona, who));
        SetTextHard(say, Titled("对 话", line.speech, 0.4f));

        // ---------- 入场：四块一起弹出，人设和对话同一批进来，不再先出人设、再出对话 ----------
        SetupEnter(new[] { counter }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { plate }, faceFrom, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { persona }, textFrom, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { say }, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In);

        s.StartCoroutine(s.SlideIn(face, faceFrom, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        yield return SlideInAll(s, new[] { counter, plate, persona, say });
        s.Float(face, 0.09f, 3.6f);

        // ---------- 台上整段 3 秒：滑入 0.2 + 停留 2.6 + 滑出 0.2，不再按字数干等 ----------
        yield return s.WaitStage(LingerSeconds);

        yield return SlideOutAll(s, new[] { counter, face, plate, persona, say }, StoryTeller.Direction.Botton, StageStyle.Distance);
        yield return s.WaitStage(StageStyle.Short);
    }

    /// <summary>
    /// 人设卡的正文：一行小标题（谁的人设，配色沿用原来那张思考卡）+ 角色卡里的 oc 人设文本。
    /// 太长时按 320 字兜底裁切（和思考卡同一口径），免得撑出卡片。
    /// </summary>
    private static string PersonaBody(string persona, string owner)
    {
        string who = string.IsNullOrWhiteSpace(owner) ? "AI" : owner.Trim();
        string head = StageStyle.SizeTag(CaptionH) + "<color=" + ThinkCaptionColor + ">" + Colorize(who + " 的人设") + "</color></size>\n";
        if (string.IsNullOrWhiteSpace(persona))
            return head + StageStyle.SizeTag(BodyH) + "<color=" + CaptionColor + ">（这张角色卡没有填人设）</color></size>";
        return head + StageStyle.SizeTag(BodyH) + "<color=#9EABBF>" + Colorize(StageStyle.Clamp(persona, 320)) + "</color></size>";
    }

    /// <summary>
    /// 这座塔在战场上偏左还是偏右，用来决定立绘放哪边（和实际 Towel 排布一致）。
    /// 返回 +1 = 立绘在左、文字在右；-1 = 立绘在右、文字在左。
    /// 拿不到那座塔时退回「左 / 右 / 右 / 左」的默认排布。
    /// </summary>
    private static float PortraitSide(int stage)
    {
        if (Towel.AllTowel.TryGetValue(stage, out Towel towel) && towel != null)
            return towel.transform.position.x <= 0f ? 1f : -1f;
        return stage == 2 || stage == 3 ? -1f : 1f;
    }
}
