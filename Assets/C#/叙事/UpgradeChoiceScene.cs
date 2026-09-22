using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 【AI 弹珠升级选择演绎】
///
/// 空槽升级进度满了之后，AI 要在「额外弹珠 / 炮塔强化 / 护盾强化」里三选一。
/// 原来只有 UI 上的一句「升级！」，玩家看不到 AI 为什么这么选。这里搬上舞台：
///   标题 + AI 名字从上方进场 → 立绘从左侧进场
///   → 三张选项卡并排从下方错开进场（每张卡中间写的就是这一项的收益）
///   → 底部思考条进场，先显示「思考中…」
///   → 思考条换成真实思考内容（结果由 MarbleManager 在**开舞台之前**请求好，演出里不再发请求）
///   → 选中的卡亮起、其余压暗 → 名字行变成「XX 选择了「YY」」→ 一起滑下去。
///
/// 升级本身不在这里生效：演出播完后由 MarbleManager 读 Choice 再生效，
/// 这样炮塔升级的连线与飘字仍然留在战场上播。
///
/// 顺序（必须守住）：**先请求 → 再开舞台 → 演完才让升级生效（连线特效）**。
/// 演出里绝对不要再发 AI 请求：非实时录制下 AVPro 的 ResumeCapture 会把 Time.timeScale 顶回 1，
/// 舞台还没播完战场就活了；而且请求期间录制是暂停的，舞台动画照样按真实时间在走却不进视频，
/// 恢复录制时画面会跳一下。请求放在开舞台之前，整段演出才能被完整连续地录下来。
/// </summary>
public class UpgradeChoiceScene : StoryScene
{
    // —— 版面（舞台可见范围约 17.8 × 10，中心为原点）——
    private const float CardW = 3.5f;
    private const float CardH = 3.8f;
    private const float Spacing = 4f;
    private const float RowY = 1.05f;         // = 卡顶 2.95 固定：卡变矮时把整排往上顶，空出来的都让给下方思考条
    private const float CardsX = 1.9f;        // 三张卡整体中心（左半边留给立绘）

    private const float FaceX = -6.7f;
    private const float FaceY = -0.8f;
    private static readonly Vector2 FaceSize = new Vector2(4.3f, 5.5f);
    private const float FaceZoom = 1.15f;
    private const float FaceFadeBottom = 0.45f;    // 下沿渐隐比例

    // 选项名（文案口径与 AIAgent.BuildUpgradeChoicePrompt 保持一致）
    // 卡面的收益文案不写死在这里：里面所有具体数值都由 BenefitFor() 从玩法代码现取
    private static readonly (int choice, string title)[] Defs =
    {
        (1, "额外弹珠"),
        (2, "炮塔强化"),
        (3, "护盾强化"),
    };

    /// <summary>卡片正文的字号（世界单位高度）：加了具体数值之后比原来收一档。</summary>
    private const float CardTextH = 0.26f;
    /// <summary>正文里"数值"那一档再小一点，跟描述文字分得开。</summary>
    private const float CardNumH = 0.23f;
    /// <summary>卡片正文的 TMP 排版框高度（世界单位）。</summary>
    private const float CardTextBoxH = 2.6f;

    /// <summary>思考条正文的字号（世界单位高度）。通用求思考正文是 0.36，这里单独收小一档。</summary>
    private const float ThinkTextH = 0.28f;
    /// <summary>思考条的排版字号（世界单位）。正文走 &lt;size&gt; 标签，这个只决定框的基础字号，两者要分开写。</summary>
    private const float ThinkBoxH = 0.27f;

    /// <summary>
    /// 思考的弹字速度倍率（1 = 原速）。Text.prefab 的逐字间隔被夹在 0.02~0.1 秒之间，
    /// 长思考算出来的间隔早就低于下限，靠 typeMaxSeconds 压不短，所以直接加快整段动画：这里 4 = 四倍速。
    /// </summary>
    private const float ThinkTypeSpeed = 4f;

    /// <summary>选好之后等卡片弹完的停顿。原来 0.8 秒里大半是空等，收紧到刚好盖住那几下弹动（0.3 / 0.35 秒）。</summary>
    private const float PickWaitSeconds = 0.35f;
    /// <summary>结论的停留：就一行短文案，原来 2.4 秒太长，压到 1.4 秒。</summary>
    private const float ConclusionHoldSeconds = 1.4f;

    private class Option
    {
        public int choice;
        /// <summary>这张卡的中心 x（结论标记要落在同一列）。</summary>
        public float cx;
        public StoryTeller.Item board;
        /// <summary>卡片顶部的选项名。</summary>
        public StoryTeller.Item title;
        /// <summary>卡片正中的收益文案。</summary>
        public StoryTeller.Item label;
        public StoryTeller.Item icon;
    }

    private readonly int owner;
    /// <summary>开舞台**之前**就请求好的选择结果（null = 请求失败，按默认项演）。</summary>
    private readonly UpgradeChoiceResult result;
    private readonly List<Option> options = new();

    /// <summary>AI 最终选的是哪一项：1 额外弹珠 / 2 炮塔强化 / 3 护盾强化。</summary>
    public int Choice { get; private set; } = 1;
    /// <summary>演出是否已播完（MarbleManager 用它决定什么时候让升级真正生效）。</summary>
    public bool Finished { get; private set; }

    /// <summary>owner 的升级选择演出。result 必须是开舞台之前就请求好的结果（演出里不再发请求）。</summary>
    public UpgradeChoiceScene(int owner, UpgradeChoiceResult result)
    {
        this.owner = owner;
        this.result = result;
    }

    public override IEnumerator Play()
    {
        try
        {
            yield return Run();
        }
        finally
        {
            Finished = true;
        }
    }

    private IEnumerator Run()
    {
        StoryTeller s = stage;
        if (s == null) yield break;

        Color accent = MapConfig.Instance != null
            ? MapConfig.Instance.GetColor(owner, MapConfig.ColorStage.Towel)
            : Color.white;
        string who = AIAgent.GetStageName(owner);

        // ---------- 建卡 ----------
        StoryTeller.Item title = Title("触 发 升 级");
        StoryTeller.Item face = Portrait(owner, SpriteEmotion.origin, new Vector2(FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item plate = Nameplate(who, new Vector2(FaceX, -4.2f), new Vector2(3.4f, 0.9f), accent);
        StoryTeller.Item think = Think(ThinkWaitingFor(who), new Vector2(CardsX, -2.82f), new Vector2(13f, 3.76f), StageStyle.FontSize(ThinkBoxH));
        // 思考弹字加快一倍：这一幕的时长基本由「等思考打完」决定，打字快了整场跟着短
        if (think != null && think.textDisplay != null && think.textDisplay.Anim != null)
            think.textDisplay.Anim.speedMultiplier = ThinkTypeSpeed;

        BuildOptions(accent);
        SetupEnter(new[] { title }, StoryTeller.Direction.Top, StageStyle.Distance, StageStyle.In);
        SetupEnter(new[] { think }, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In);

        // ---------- 入场：全部并行（立绘、名牌、思考条、三张卡同时进），不再一张张等 ----------
        var entering = new List<StoryTeller.Item> { title, plate, think };
        for (int i = 0; i < options.Count; i++)
        {
            Option o = options[i];
            StoryTeller.Direction from = i == 0
                ? StoryTeller.Direction.Left
                : (i == options.Count - 1 ? StoryTeller.Direction.Right : StoryTeller.Direction.Botton);
            SetupEnter(new[] { o.board, o.title, o.label, o.icon }, from, StageStyle.Distance, StageStyle.In);

            entering.Add(o.board);
            entering.Add(o.title);
            entering.Add(o.label);
            if (o.icon != null) entering.Add(o.icon);
        }

        s.StartCoroutine(s.SlideIn(face, StoryTeller.Direction.Left, StageStyle.Distance, StageStyle.In, 0f, new Vector2(0.9f, 0.9f)));
        yield return SlideInAll(s, entering);
        s.Float(face, 0.08f, 3.2f);   // 立绘轻轻浮动：揭晓之前画面不至于像张死图
        yield return s.WaitStage(StageStyle.Short);

        // ---------- 选择结果 ----------
        // 请求在开舞台之前就发完了（MarbleManager.AIUpgradeSequence）：
        // 演出里只负责「先出思考、再揭晓」。这里绝不能再去请求 AI ——
        // 请求会暂停录制，恢复录制时 AVPro 会把 Time.timeScale 顶回 1，舞台还没播完战场就活了。
        int choice = result != null ? result.choice : 1;
        string thinking = result != null ? result.thinking : "";

        Choice = choice;

        // ---------- 先出思考 ----------
        bool hasThinking = !string.IsNullOrWhiteSpace(thinking);
        SetText(think, ThinkBody(thinking, who, 320, ThinkTextH), hasThinking);
        if (hasThinking) yield return WaitTextIn(s, think);            // 思考打完立刻出选择结果
        else yield return s.WaitStage(StageStyle.Hold);

        // ---------- 选中 / 压暗 ----------
        Option picked = null;
        var dim = new List<StoryTeller.Item>();
        foreach (Option o in options)
        {
            if (o.choice == choice) picked = o;
            else
            {
                dim.Add(o.board);
                dim.Add(o.title);
                dim.Add(o.label);
                if (o.icon != null) dim.Add(o.icon);
            }
        }
        Highlight(picked?.board, dim, accent);
        if (picked?.label != null) s.SetHighlight(picked.label, true, accent);

        // 选中的那张轻轻弹一下（前端里那种 selected 反馈）
        if (picked != null)
        {
            var small = new Vector2(0.94f, 0.94f);
            if (picked.board != null) s.StartCoroutine(s.Pop(picked.board, small, Vector2.one, 0.3f));
            if (picked.label != null) s.StartCoroutine(s.Pop(picked.label, small, Vector2.one, 0.3f));
            if (picked.title != null) s.StartCoroutine(s.Pop(picked.title, small, Vector2.one, 0.3f));
            if (picked.icon != null) s.StartCoroutine(s.Pop(picked.icon, new Vector2(0.7f, 0.7f), Vector2.one, 0.35f));
        }
        yield return s.WaitStage(PickWaitSeconds);

        // ---------- 结论 ----------
        StoryTeller.Item conclusion = Label(who + " 选择了「" + TitleOf(choice) + "」",
            new Vector2(CardsX, 3.45f), new Vector2(12f, 0.7f), accent, StageStyle.FontSize(0.46f));
        StoryTeller.Item badge = null;
        if (picked != null)
        {
            // ✔ 挂在选中那张卡的下沿内侧：卡片正中始终留给这一项的收益
            badge = Label("<color=#FFE7A3>✔</color> 就选它",
                new Vector2(picked.cx, RowY - CardH * 0.5f + 0.3f), new Vector2(2.6f, 0.45f), StageStyle.Mark, StageStyle.FontSize(0.26f));
        }
        if (conclusion != null)
            s.StartCoroutine(s.SlideIn(conclusion, StoryTeller.Direction.Top, StageStyle.Distance * 0.4f, 0.3f));
        if (badge != null)
            yield return s.SlideIn(badge, StoryTeller.Direction.Botton, StageStyle.Distance * 0.3f, 0.35f, 0f, new Vector2(0.8f, 0.8f));
        yield return s.WaitStage(ConclusionHoldSeconds);

        // ---------- 出场 ----------
        var all = new List<StoryTeller.Item> { title, face, plate, think, conclusion, badge };
        foreach (Option o in options)
        {
            all.Add(o.board);
            all.Add(o.title);
            all.Add(o.label);
            if (o.icon != null) all.Add(o.icon);
        }
        yield return SlideOutAll(s, all, StoryTeller.Direction.Botton, StageStyle.Distance);
        //yield return s.WaitStage(StageStyle.Short);
    }

    private void BuildOptions(Color accent)
    {
        StoryTeller s = stage;
        if (s == null) return;

        options.Clear();
        for (int i = 0; i < Defs.Length; i++)
        {
            var def = Defs[i];
            float cx = CardsX + (i - 1) * Spacing;
            var o = new Option { choice = def.choice, cx = cx };

            // 底板：Mesh.prefab 的圆角容器（不挂图片，纯当卡片底），描边带一点阵营色
            o.board = Picture(null, new Vector2(cx, RowY), new Vector2(CardW, CardH), Color.white, true);
            if (o.board != null)
            {
                o.board.sortingOrder = 1;
                o.board.containerColor = StageStyle.PanelBg;
                o.board.wallColor = Color.Lerp(StageStyle.PanelWall, accent, 0.5f);
                s.ApplyItem(o.board);
            }

            // 标题：贴在卡片上沿内侧，用阵营色
            o.title = Label("<b>" + def.title + "</b>",
                new Vector2(cx, RowY + CardH * 0.5f - 0.4f), new Vector2(CardW - 0.6f, 0.6f), accent, StageStyle.FontSize(0.38f));
            if (o.title != null)
            {
                o.title.sortingOrder = 4;
                s.ApplyItem(o.title);
            }

            // 卡片正中：这一项到底给什么（容器透明，正好压在底板中央）
            o.label = Card(StageStyle.SizeTag(CardTextH) + "<color=#C7CEDD>" + BenefitFor(def.choice) + "</color></size>",
                new Vector2(cx, RowY - 0.2f), new Vector2(CardW - 0.6f, CardTextBoxH),
                StageStyle.PanelBg, StageStyle.PanelWall, StageStyle.TextMain, false, StageStyle.Corner, StageStyle.FontSize(CardTextH));
            if (o.label != null)
            {
                o.label.sortingOrder = 3;
                s.ApplyItem(o.label);
            }

            // 图标：在框里、标题下面（不再是飘在卡片上沿外面）
            Sprite iconSprite = s.UpgradeIcon(def.choice);
            if (iconSprite != null)
            {
                o.icon = Picture(iconSprite, new Vector2(cx, RowY + CardH * 0.5f - 0.95f), new Vector2(0.85f, 0.85f), Color.white);
                if (o.icon != null)
                {
                    o.icon.sortingOrder = 5;
                    s.ApplyItem(o.icon);
                }
            }

            options.Add(o);
        }

        // 入场：先统一给一套默认（下面每张卡再按左/中/右改成各自的进场方向）
        var all = new List<StoryTeller.Item>();
        foreach (Option o in options)
        {
            all.Add(o.board);
            all.Add(o.title);
            all.Add(o.label);
            if (o.icon != null) all.Add(o.icon);
        }
        SetupEnter(all, StoryTeller.Direction.Botton, StageStyle.Distance, StageStyle.In);
    }

    /// <summary>正文里的一个数值：小一档 + 暖金色，跟描述文字分得开。</summary>
    private static string Num(string s)
        => StageStyle.SizeTag(CardNumH) + "<color=#FFE7A3>" + s + "</color></size>";

    /// <summary>
    /// 卡片正中的收益文案。**里面每一个数值都是从玩法代码现取的**（Towel 的每级倍率 / MarbleManager 的弹珠起手值 /
    /// ReactionSystem 的瞄准误差基准），不在这份文案里写死 —— 以后调平衡，卡片上的数字跟着变，不用两头改。
    /// 拿不到实例时退回各自的默认值。
    /// </summary>
    private string BenefitFor(int choice)
    {
        Towel t = Towel.AllTowel.TryGetValue(owner, out Towel tw) ? tw : null;

        switch (choice)
        {
            case 1:
            {
                int now = MarbleManager.Instance != null ? MarbleManager.Instance.GetMarbleCount(owner) : 0;
                return "弹珠数 " + Num(now + " → " + (now + 2) + " 颗")
                     + "\n\n道具全靠弹珠产出";
            }
            case 2:
            {
                float radius = t != null ? t.upgradedBulletRadiusScale : 1.7f;
                float impact = t != null ? t.upgradedBulletImpactScale : 1.6f;
                float move = t != null ? t.moveRangePerLevel : 1f;
                float speed = t != null ? t.moveSpeedPerLevel : 0.1f;
                return "最大移动距离 " + Num("+" + move.ToString("0.##")) + "\n"
                     + "移动速度 " + Num("+" + speed.ToString("0.##") + "/秒") + "\n"
                     + "护卫极限转速 " + Num("×" + Towel.GuardSpeedPerLevel.ToString("0.##")) + "\n"
                     + "子弹半径 " + Num("×" + radius.ToString("0.##")) + "／大球动量 " + Num("×" + impact.ToString("0.##"));
            }
            default:
            {
                // 每次升级累加 基准/2^N（N 从 0 开始）：卡片写「当前 → 升级后」（新增的一份逐级减半、总时长封顶 2 倍基准）
                float now = t != null ? t.ShieldInvincibleTimeAt(t.shieldUpgradeOwned) : 0f;
                float next = t != null ? t.ShieldInvincibleTimeAt(t.shieldUpgradeOwned + 1) : 0f;
                return "护盾破碎后炮塔\n<color=#FFFFFF>无敌时间 " + Num(now.ToString("0.##") + " → " + next.ToString("0.##") + " 秒") + "</color>"
                     + "\n\n无视敌方子弹\n与大球伤害";
            }
        }
    }

    private static string TitleOf(int choice)
    {
        foreach (var def in Defs)
            if (def.choice == choice) return def.title;
        return "额外弹珠";
    }
}
