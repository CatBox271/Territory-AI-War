using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 【AI 弹珠升级选择演绎】
///
/// 空槽升级进度满了之后，AI 要在「额外弹珠 / 炮塔强化 / 护盾强化」里三选一。
/// 原来只有 UI 上的一句「升级！」，玩家看不到 AI 为什么这么选。这里搬上舞台：
///   标题 + AI 名字从上方进场 → 立绘从左侧进场
///   → 三张选项卡并排从下方错开进场（每张卡中间写的就是这一项的收益）
///   → 底部思考条进场，先显示「思考中…」
///   → 等 AIAgent 的两步选择请求回来 → 思考条换成真实思考内容
///   → 选中的卡亮起、其余压暗 → 名字行变成「XX 选择了「YY」」→ 一起滑下去。
///
/// 升级本身不在这里生效：演出播完后由 MarbleManager 读 Choice 再生效，
/// 这样炮塔升级的连线与飘字仍然留在战场上播。
/// </summary>
public class UpgradeChoiceScene : StoryScene
{
    // —— 版面（舞台可见范围约 17.8 × 10，中心为原点）——
    private const float CardW = 3.5f;
    private const float CardH = 4.3f;
    private const float Spacing = 4f;
    private const float RowY = -0.35f;
    private const float CardsX = 1.9f;        // 三张卡整体中心（左半边留给立绘）

    private const float FaceX = -6.7f;
    private const float FaceY = -0.8f;
    private static readonly Vector2 FaceSize = new Vector2(4.3f, 5.5f);
    private const float FaceZoom = 1.15f;
    private const float FaceFadeBottom = 0.45f;    // 下沿渐隐比例

    // 选项名与收益（文案口径与 AIAgent.BuildUpgradeChoicePrompt 保持一致）
    // benefit 是卡片正中间那块内容：先一句「立刻给什么」，空一行再说长期收益
    private static readonly (int choice, string title, string benefit)[] Defs =
    {
        (1, "额外弹珠", "立即生成并发射\n<color=#FFFFFF>一枚你的新弹珠</color>\n\n长期弹珠资源与\n倍乘收益更厚"),
        (2, "炮塔强化", "后坐力提升，<color=#FFFFFF>子弹\n显示半径变大</color>\n\n命中大球动量冲击更强\n护卫极限转速翻倍\n炮塔更精准"),
        (3, "护盾强化", "护盾破碎后炮塔\n<color=#FFFFFF>进入无敌时间</color>\n\n无视敌方子弹\n与大球伤害"),
    };

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
    private readonly List<Option> options = new();

    /// <summary>AI 最终选的是哪一项：1 额外弹珠 / 2 炮塔强化 / 3 护盾强化。</summary>
    public int Choice { get; private set; } = 1;
    /// <summary>演出是否已播完（MarbleManager 用它决定什么时候让升级真正生效）。</summary>
    public bool Finished { get; private set; }

    public UpgradeChoiceScene(int owner)
    {
        this.owner = owner;
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
        StoryTeller.Item title = Title("升 级 选 择");
        StoryTeller.Item face = Portrait(owner, SpriteEmotion.origin, new Vector2(FaceX, FaceY), FaceSize, FaceZoom, FaceFadeBottom);
        StoryTeller.Item plate = Nameplate(who, new Vector2(FaceX, -4.2f), new Vector2(3.4f, 0.9f), accent);
        StoryTeller.Item think = Think(ThinkWaitingFor(who), new Vector2(CardsX, -3.7f), new Vector2(13f, 2.6f), StageStyle.FontSize(0.3f));

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
        s.Float(face, 0.08f, 3.2f);   // 立绘轻轻浮动：等 AI 的时候画面不至于像张死图
        yield return s.WaitStage(StageStyle.Short);

        // ---------- 问 AI ----------
        // AIAgent 内部会暂停录制；舞台时钟靠 RealTimeUpdate 兜底，动画不会卡住。
        int choice = 1;
        string thinking = "";
        AIAgent agent = AIAgent.Instance;
        if (agent != null)
        {
            Task<UpgradeChoiceResult> task = agent.UpgradeChoiceRequestAsync(owner);
            while (!task.IsCompleted) yield return s.WaitForCaptureUpdate();   // 等外部结果，不占用帧锁时钟
            if (!task.IsFaulted && !task.IsCanceled && task.Result != null)
            {
                choice = task.Result.choice;
                thinking = task.Result.thinking;
            }
        }

        Choice = choice;

        // ---------- 先出思考 ----------
        bool hasThinking = !string.IsNullOrWhiteSpace(thinking);
        SetText(think, ThinkBody(thinking, who), hasThinking);
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
        yield return s.WaitStage(0.8f);

        // ---------- 结论 ----------
        StoryTeller.Item conclusion = Label(who + " 选择了「" + TitleOf(choice) + "」",
            new Vector2(CardsX, 3.15f), new Vector2(12f, 0.7f), accent, StageStyle.FontSize(0.46f));
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
        yield return s.WaitStage(2.4f);

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
        yield return s.WaitStage(StageStyle.Short);
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
            o.label = Card(StageStyle.SizeTag(0.28f) + "<color=#C7CEDD>" + def.benefit + "</color></size>",
                new Vector2(cx, RowY - 0.2f), new Vector2(CardW - 0.6f, 2.6f),
                StageStyle.PanelBg, StageStyle.PanelWall, StageStyle.TextMain, false, StageStyle.Corner, StageStyle.FontSize(0.28f));
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

    private static string TitleOf(int choice)
    {
        foreach (var def in Defs)
            if (def.choice == choice) return def.title;
        return "额外弹珠";
    }
}
