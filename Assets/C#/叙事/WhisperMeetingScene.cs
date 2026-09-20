using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 【AI 秘密会晤】
///
/// 悄悄话会被 WhisperManager 记成一行顶部横幅，玩家基本看不见；这里把它搬上舞台。
/// 版面：正中大标题，左右各一张大立绘（下沿压阵营色名牌），消息分左右两端（像聊天记录）：
///   左上：谁在悄悄说什么（左对齐）→ 中：「AI 的思考」（居中旁白）→ 右下：对方怎么回答（右对齐）。
/// 思考与回复由 AIAgent.WhisperReplyAsync 取好之后才建这个场景，这里只负责节奏。
/// </summary>
public class WhisperMeetingScene : StoryScene
{
    // —— 版面（舞台可见范围约 17.8 × 10，中心为原点）——
    private const float FaceX = 6.7f;
    private const float FaceY = -0.9f;
    private static readonly Vector2 FaceSize = new Vector2(4.9f, 6.2f);
    private const float FaceZoom = 1.15f;
    private const float FaceFadeBottom = 0.45f;    // 下沿渐隐比例

    private const float BoxW = 8.2f;
    private const float MsgW = 7.2f;      // 消息卡宽度
    private const float MsgX = 1.7f;      // 消息卡偏离中线的量：悄悄话在左、回答在右
    private const float WhisperY = 1.9f;
    private const float ThinkY = -1.05f;
    private const float ReplyY = -3.35f;

    /// <summary>
    /// 「全部显示完之后」在台上停留的时长倍率：读完时长和出场前那一拍都乘它。
    /// 原来 = 1（ReadTime 1.8~7 秒 + StageStyle.Short），现在砍一半。
    /// </summary>
    private const float TailHoldScale = 0.5f;

    private readonly int sender;
    private readonly int target;
    private readonly string whisper;
    private readonly string thinking;
    private readonly string reply;

    public WhisperMeetingScene(int sender, int target, string whisper, string thinking, string reply)
    {
        this.sender = sender;
        this.target = target;
        this.whisper = whisper ?? "";
        this.thinking = thinking ?? "";
        this.reply = reply ?? "";
    }

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null) yield break;

        Color senderColor = Accent(sender);
        Color targetColor = Accent(target);
        string senderName = AIAgent.GetStageName(sender);
        string targetName = AIAgent.GetStageName(target);

        // ---------- 建卡 ----------
        StoryTeller.Item title = Title("秘 密 会 晤");

        StoryTeller.Item leftFace = Portrait(sender, SpriteEmotion.origin, new Vector2(-FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item leftPlate = Nameplate(senderName, new Vector2(-FaceX, -4.35f), new Vector2(3.4f, 0.9f), senderColor);

        StoryTeller.Item rightFace = Portrait(target, SpriteEmotion.origin, new Vector2(FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item rightPlate = Nameplate(targetName, new Vector2(FaceX, -4.35f), new Vector2(3.4f, 0.9f), targetColor);

        // 消息分左右（像聊天记录的两端）：发话人的悄悄话贴在左边立绘这一侧，
        // 对方的回答贴右边立绘那一侧；两张框的文字**都左对齐**（右对齐时短句会被推到右边、
        // 换行后的首行也不齐，读起来别扭），中间的「AI 的思考」是旁白，仍然居中。
        StoryTeller.Item whisperBox = Panel("", new Vector2(-MsgX, WhisperY), new Vector2(MsgW, 2.4f), StageStyle.FontSize(0.34f));
        StoryTeller.Item think = Think(ThinkWaitingFor(targetName), new Vector2(0f, ThinkY), new Vector2(BoxW, 3.2f), StageStyle.FontSize(BodyH));
        StoryTeller.Item replyBox = Panel("", new Vector2(MsgX, ReplyY), new Vector2(MsgW, 2.4f), StageStyle.FontSize(0.34f));

        SetupEnter(new[] { title }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { leftPlate, whisperBox }, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { rightPlate, think }, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { replyBox }, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In);

        // ---------- 入场：全部并行（两个立绘各自带一点缩放），不再一个一个等 ----------
        s.StartCoroutine(s.SlideIn(leftFace, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        s.StartCoroutine(s.SlideIn(rightFace, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        yield return SlideInAll(s, new[] { title, leftPlate, rightPlate, think });   // 两个内容框不在这批里
        s.Float(leftFace, 0.08f, 3.6f);
        s.Float(rightFace, 0.08f, 3.6f);
        yield return s.WaitStage(StageStyle.Short);

        // ---------- 只有内容顺序：悄悄话 → 思考 → 回答（内容框都跟内容一起出现）----------
        PortraitFocus(leftFace, true);      // 发话人说话：自己原样，对方整体调淡
        PortraitFocus(rightFace, false);
        s.StartCoroutine(s.SlideIn(whisperBox, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In, 0.2f));
        SetText(whisperBox, Titled(senderName + " 悄悄说", whisper, 0.34f), true);
        yield return WaitTextIn(s, whisperBox);   // 悄悄话打完就换思考，不按字数干等

        PortraitFocus(leftFace, false);     // 换成对方在思考 / 回答
        PortraitFocus(rightFace, true);
        bool hasThinking = !string.IsNullOrWhiteSpace(thinking);
        SetText(think, ThinkBody(thinking, targetName), hasThinking);
        if (hasThinking) yield return WaitTextIn(s, think);            // 思考打完立刻出回答
        else yield return s.WaitStage(StageStyle.Hold);

        // 思考一播完立刻开内容：框子滑入和打字同时开始，中间不再停（回答从右边立绘那一侧进来）
        s.StartCoroutine(s.SlideIn(replyBox, StoryTeller.Direction.Right, StageStyle.Distance, StageStyle.In, 0.2f));
        SetText(replyBox, Titled(targetName + " 回答", reply, 0.34f), true);

        // 全部显示完之后在台上停的时长：读完时长 × 这个倍率，原来 1，现在砍一半
        yield return s.WaitStage(ReadTime(reply) * TailHoldScale);

        // ---------- 出场 ----------
        yield return s.WaitStage(StageStyle.Short * TailHoldScale);
        yield return SlideOutAll(s,
            new[] { title, leftFace, leftPlate, rightFace, rightPlate, whisperBox, think, replyBox },
            StoryTeller.Direction.Botton, StageStyle.Distance);
        //yield return s.WaitStage(StageStyle.Short);
    }

    private static Color Accent(int owner)
    {
        return MapConfig.Instance != null
            ? MapConfig.Instance.GetColor(owner, MapConfig.ColorStage.Towel)
            : Color.white;
    }
}
