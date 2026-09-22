using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 【AI 秘密会晤】—— 多轮版：你 / 他 交替，最多 5 条「说」（你1 他1 你2 他2 你3）。
///
/// 版面沿用原来那套（正中大标题、左右两张带阵营色名牌的大立绘、消息卡左右分边、思考居中当旁白），
/// 把「一条悄悄话 + 一条回答」换成**三格滚动窗口**：
///   第 1 格（上）＝ 上一句「说」：新的一轮开始时，它从第 3 格滑上来（旧的第 1 格边升边消失）
///   第 2 格（中）＝ 当前的「想」：居中旁白，打字；**打完才出这一轮的「说」**
///   第 3 格（下）＝ 当前的「说」：说话人那一侧的气泡
/// 每一轮：上一轮的「想」淡出 → 上一句「说」上移 0.2s → 新的「想」→ 新的「说」。
/// 其中「想」只是旁白、不留场；两条「说」交替占第 1 / 第 3 格，所以画面上永远是「上一句 + 这一轮」。
///
/// 内容（含每条「说」的拟人化）全部由 AIAgent 取好之后才建这个场景，这里只负责节奏和位移。
/// </summary>
public class WhisperMeetingScene : StoryScene
{
    /// <summary>会晤里的一条「说」（外加说话人当时的「想」）。stage = 这句话是谁说的。</summary>
    public class Line
    {
        public int stage;
        public string think = "";
        public string say = "";
    }

    // —— 版面（舞台可见范围约 17.8 × 10，中心为原点）——
    private const float FaceX = 6.7f;
    private const float FaceY = -0.9f;
    private static readonly Vector2 FaceSize = new Vector2(4.9f, 6.2f);
    private const float FaceZoom = 1.15f;
    private const float FaceFadeBottom = 0.45f;    // 下沿渐隐比例

    private const float BoxW = 8.2f;
    private const float MsgW = 7.2f;      // 消息卡宽度
    private const float MsgX = 1.7f;      // 消息卡偏离中线的量：发起方在左、对方在右
    private const float BoxH = 2.3f;      // 消息卡高度

    private const float SayTopY = 2.25f;    // 第 1 格：上一句「说」
    private const float ThinkY = -1.0f;     // 第 2 格：当前的「想」
    private const float SayBottomY = -3.5f; // 第 3 格：当前的「说」

    /// <summary>「想」淡出 / 上一句上移的时长（秒）。</summary>
    private const float ShiftTime = 0.2f;

    /// <summary>
    /// 「想」的逐字速度倍率：会晤里的「想」就是 AI 的【分析】，常常几百字，
    /// 原速打要十几秒 —— 那段时间就是「上一句说完了、下一句还不出来」的空档。
    /// 和升级选择那场一个口径（那场也是 4 倍速）。
    /// </summary>
    private const float ThinkTypeSpeed = 4f;

    /// <summary>「想」最多显示多少字（超出的按句末标点收尾并加省略号）。</summary>
    private const int ThinkMaxChars = 200;

    /// <summary>
    /// 一句说完 → 下一个人开口之间的节拍（秒）。
    /// 原来这里是 ReadTime(say) × 0.5（1.8~7 秒的一半，最长 3.5 秒）——
    /// 但上一句这时已经上移到第 1 格还留在画面上，不需要再"读"一遍，
    /// 那段等待就是用户说的「最后一个显示结束到下一个回复太久」。
    /// </summary>
    private const float LineGap = 0.35f;

    /// <summary>全部说完之后、出场之前停留多久（秒）。</summary>
    private const float TailHold = 1.2f;

    private readonly int sender;
    private readonly int target;
    private readonly List<Line> lines = new();

    public WhisperMeetingScene(int sender, int target, IEnumerable<Line> lines)
    {
        this.sender = sender;
        this.target = target;
        if (lines == null) return;
        foreach (Line l in lines)
            if (l != null && !string.IsNullOrWhiteSpace(l.say)) this.lines.Add(l);
    }

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null || lines.Count == 0) yield break;

        Color senderColor = Accent(sender);
        Color targetColor = Accent(target);
        string senderName = AIAgent.GetStageName(sender);
        string targetName = AIAgent.GetStageName(target);

        // 建卡时把所有出现过的东西都记下来，出场时一起收
        var spawned = new List<StoryTeller.Item>();

        // ---------- 标题 + 双方立绘 + 名牌 ----------
        StoryTeller.Item title = Title("秘 密 会 晤");
        StoryTeller.Item leftFace = Portrait(sender, SpriteEmotion.origin, new Vector2(-FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item leftPlate = Nameplate(senderName, new Vector2(-FaceX, -4.35f), new Vector2(3.4f, 0.9f), senderColor);
        StoryTeller.Item rightFace = Portrait(target, SpriteEmotion.origin, new Vector2(FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item rightPlate = Nameplate(targetName, new Vector2(FaceX, -4.35f), new Vector2(3.4f, 0.9f), targetColor);
        spawned.Add(title);
        spawned.Add(leftFace);
        spawned.Add(leftPlate);
        spawned.Add(rightFace);
        spawned.Add(rightPlate);

        SetupEnter(new[] { title }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { leftPlate }, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { rightPlate }, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In);

        s.StartCoroutine(s.SlideIn(leftFace, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        s.StartCoroutine(s.SlideIn(rightFace, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        yield return SlideInAll(s, new[] { title, leftPlate, rightPlate });
        s.Float(leftFace, 0.08f, 3.6f);
        s.Float(rightFace, 0.08f, 3.6f);
        yield return s.WaitStage(StageStyle.Short);

        // ---------- 第 1 格：谁先说，谁的气泡就先占上面那格 ----------
        StoryTeller.Item top = null;
        StoryTeller.Item mid = null;
        StoryTeller.Item bot = null;

        int i = 0;
        top = MakeSay(s, lines[0], SayTopY, spawned);
        SetupEnter(new[] { top }, SideOf(lines[0].stage), StageStyle.Distance, StageStyle.In);
        FocusOn(leftFace, rightFace, lines[0].stage, true);
        s.StartCoroutine(s.SlideIn(top, top.enterFrom, top.enterDistance, top.enterDuration, 0.2f));
        SetText(top, SayText(lines[0], sender, senderName, targetName), true);
        yield return WaitTextIn(s, top);
        i++;

        // ---------- 之后每一条：想（第 2 格）→ 说（第 3 格），然后给下一轮腾位置 ----------
        while (i < lines.Count)
        {
            Line cur = lines[i];

            if (!string.IsNullOrWhiteSpace(cur.think))
            {
                mid = Think("", new Vector2(0f, ThinkY), new Vector2(BoxW, 3.2f), StageStyle.FontSize(BodyH));
                spawned.Add(mid);
                // 「想」加速打（原来原速，几百字的思考要打十几秒，全卡在下一句之前）
                if (mid != null && mid.textDisplay != null && mid.textDisplay.Anim != null)
                    mid.textDisplay.Anim.speedMultiplier = ThinkTypeSpeed;
                SetupEnter(new[] { mid }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
                s.StartCoroutine(s.SlideIn(mid, mid.enterFrom, mid.enterDistance, mid.enterDuration, 0.2f));
                SetText(mid, ThinkBody(cur.think, AIAgent.GetStageName(cur.stage), ThinkMaxChars), true);
                yield return WaitTextIn(s, mid);      // 想完再说
            }

            FocusOn(leftFace, rightFace, cur.stage, true);
            bot = MakeSay(s, cur, SayBottomY, spawned);
            SetupEnter(new[] { bot }, SideOf(cur.stage), StageStyle.Distance, StageStyle.In);
            s.StartCoroutine(s.SlideIn(bot, bot.enterFrom, bot.enterDistance, bot.enterDuration, 0.2f));
            SetText(bot, SayText(cur, sender, senderName, targetName), true);
            yield return WaitTextIn(s, bot);
            i++;

            if (i >= lines.Count) break;   // 最后一条「说」说完就直接收场，不再等他

            // ---------- 过渡：这一轮的「想」淡出 → 这一句「说」上移到第 1 格 ----------
            yield return s.WaitStage(LineGap);

            if (mid != null)
            {
                s.StartCoroutine(s.SlideOut(mid, StoryTeller.Direction.Botton, StageStyle.Distance, ShiftTime));
                mid = null;
            }
            if (top != null)   // 更早的那句「说」：边往上走边消失
                s.StartCoroutine(s.SlideOut(top, StoryTeller.Direction.Top, StageStyle.Distance, ShiftTime));

            if (bot != null)
            {
                Vector2 from = bot.position;
                Vector2 to = new Vector2(SayXOf(cur.stage), SayTopY);
                s.StartCoroutine(Glide(s, bot, from, to, ShiftTime));
                top = bot;     // 它变成新的第 1 格
                bot = null;
            }
            yield return s.WaitStage(ShiftTime);
        }

        // ---------- 停一拍 → 出场 ----------
        yield return s.WaitStage(TailHold);
        yield return SlideOutAll(s, spawned, StoryTeller.Direction.Botton, StageStyle.Distance);
    }

    // ==================== 小工具 ====================

    private static Color Accent(int owner)
    {
        return MapConfig.Instance != null
            ? MapConfig.Instance.GetColor(owner, MapConfig.ColorStage.Towel)
            : Color.white;
    }

    /// <summary>气泡停在哪一边：发起方的贴左边立绘，对方的贴右边立绘。</summary>
    private float SayXOf(int stage) => stage == sender ? -MsgX : MsgX;

    private StoryTeller.Direction SideOf(int stage)
        => stage == sender ? StoryTeller.Direction.Left : StoryTeller.Direction.Right;

    /// <summary>谁在说话，就把谁的立绘提亮，另一个压暗。</summary>
    private void FocusOn(StoryTeller.Item leftFace, StoryTeller.Item rightFace, int stage, bool speaking)
    {
        if (!speaking) return;
        PortraitFocus(leftFace, stage == sender);
        PortraitFocus(rightFace, stage == target);
    }

    /// <summary>建一条「说」的气泡卡（先空着，内容随后 SetText）。</summary>
    private StoryTeller.Item MakeSay(StoryTeller s, Line line, float y, List<StoryTeller.Item> spawned)
    {
        StoryTeller.Item item = Panel("", new Vector2(SayXOf(line.stage), y), new Vector2(MsgW, BoxH), StageStyle.FontSize(0.34f));
        if (item != null && spawned != null) spawned.Add(item);
        return item;
    }

    private static string SayText(Line line, int sender, string senderName, string targetName)
    {
        string who = line.stage == sender ? senderName : targetName;
        string caption = line.stage == sender ? who + " 悄悄说" : who + " 回答";
        // 展示用文字里不带 [emo:xxx]（那是给立绘/状态区用的记号），历史与 tool result 里保留
        string shown = AIAgent.ExtractEmotion(line.say, out _, out _).Trim();
        if (string.IsNullOrWhiteSpace(shown)) shown = "（无回复）";
        return Titled(caption, shown, 0.34f);
    }

    /// <summary>
    /// 把一块卡从 from 滑到 to（走舞台时钟，录像里速度确定）。
    /// StoryTeller 只有轴对齐的 SlideIn / SlideOut，气泡换边时是斜着走的，这里自己补一个；
    /// 走完要把 item.position 一起改掉，后面 SlideOut / 再上移才是从这个新位置算。
    /// </summary>
    private static IEnumerator Glide(StoryTeller s, StoryTeller.Item item, Vector2 from, Vector2 to, float duration)
    {
        if (s == null || item == null) yield break;

        if (duration <= 0f)
        {
            item.position = to;
            item.SetLocalPosition(to);
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            yield return s.WaitForCaptureUpdate();
            t += s.ClockDelta;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
            item.SetLocalPosition(Vector2.Lerp(from, to, k));
        }

        item.position = to;
        item.SetLocalPosition(to);
    }
}
