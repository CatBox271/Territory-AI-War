using System.Collections;
using UnityEngine;

/// <summary>
/// 【游戏正式开始】收尾横幅：四家都放完狠话之后，正中一行大字 + 一行提示，
/// 提示观众在弹幕里打出自己支持的颜色。播完舞台自己关（背景淡出）。
/// </summary>
public class GameStartBannerScene : StoryScene
{
    private static readonly Color HeadColor = StageStyle.Mark;

    /// <summary>大字停留的秒数。</summary>
    private const float Hold = 4.2f;

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null) yield break;

        StoryTeller.Item head = Label(
            StageStyle.SizeTag(0.95f) + "<color=#" + ColorUtility.ToHtmlStringRGB(HeadColor) + "><b>游戏正式开始</b></color></size>",
            new Vector2(0f, 0.95f), new Vector2(16f, 1.5f), HeadColor, StageStyle.FontSize(0.95f));

        StoryTeller.Item sub = Label(
            StageStyle.SizeTag(0.5f) + "<color=#C9D4E6>在弹幕里打出你支持的颜色吧</color></size>",
            new Vector2(0f, -0.15f), new Vector2(16f, 1f), StageStyle.TextDim, StageStyle.FontSize(0.5f));

        SetupEnter(new[] { head }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { sub }, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In);

        s.StartCoroutine(s.SlideIn(sub, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In));
        yield return s.SlideIn(head, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.82f, 0.82f));
        yield return s.WaitStage(Hold);

        yield return SlideOutAll(s, new[] { head, sub }, StoryTeller.Direction.Botton, StageStyle.Distance);
    }
}
