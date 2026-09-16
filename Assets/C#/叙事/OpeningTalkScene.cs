using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 【开场放狠话】
///
/// 开局第一轮各阵营的赛前宣言。原来的做法是四座炮塔同时飘字，互相盖住；
/// 这里改成一个人一个人上台，版面按前端的思路排：
///   正中一行大标题 → 左侧一张大立绘（下沿压一块阵营色名牌）
///   → 右栏两张卡：上面是「AI 的思考」，下面是「狠话」（左对齐、带内边距的正文卡）
///   → 每个人先出思考、再出狠话，读完停一拍再一起滑下去，换下一位。
/// </summary>
public class OpeningTalkScene : StoryScene
{
    /// <summary>一个人的一轮上台。</summary>
    public class Line
    {
        public int owner;
        public string thinking = "";
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

    private readonly List<Line> lines;

    public OpeningTalkScene(List<Line> lines)
    {
        this.lines = lines;
    }

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null || lines == null || lines.Count == 0) yield break;

        StoryTeller.Item title = Title("出 厂 角 色", 4.05f, 0.95f);
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

        // 文字那一侧：思考（上）+ 对话（下）
        StoryTeller.Item think = Think(ThinkWaitingFor(who), new Vector2(TextX * side, ThinkY), new Vector2(TextW, 5f), StageStyle.FontSize(BodyH));
        StoryTeller.Item say = Panel("", new Vector2(TextX * side, SayY), new Vector2(TextW, 2.6f), StageStyle.FontSize(0.4f));

        // ---------- 入场：全部并行，各自方向，不再一个一个等 ----------
        SetupEnter(new[] { counter }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { plate }, faceFrom, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { think }, textFrom, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { say }, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In);

        s.StartCoroutine(s.SlideIn(face, faceFrom, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        yield return SlideInAll(s, new[] { counter, plate, think });   // 内容框不在这批里：它不能提前出现
        s.Float(face, 0.09f, 3.6f);
        yield return s.WaitStage(StageStyle.Short);

        // ---------- 只有这两步是顺序的：先出思考，再出对话 ----------
        bool hasThinking = !string.IsNullOrWhiteSpace(line.thinking);
        SetText(think, ThinkBody(line.thinking, who), hasThinking);
        if (hasThinking) yield return WaitTextIn(s, think);            // 思考真的打完字就走，不再按字数干等
        else yield return s.WaitStage(StageStyle.Hold);

        // 思考一播完立刻开内容：框子滑入和打字同时开始，中间不再停
        s.StartCoroutine(s.SlideIn(say, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In, 0.2f));
        SetText(say, Titled("对 话", line.speech, 0.4f), true);
        yield return s.WaitStage(ReadTime(line.speech, 0.5f, 3.5f));   // 狠话停留短一档：别让每句话都吊着

        // 说完停一下再走：一句话刚出来就滑下去，观众读不完
        yield return s.WaitStage(0.25f);
        yield return SlideOutAll(s, new[] { counter, face, plate, think, say }, StoryTeller.Direction.Botton, StageStyle.Distance);
        yield return s.WaitStage(StageStyle.Short);
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
