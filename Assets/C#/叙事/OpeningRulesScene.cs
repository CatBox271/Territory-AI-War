using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 【开场介绍】用户定稿的分镜：六行，时长 7 / 9 / 5 / 13 / 5 / 9 秒。
///
/// 视觉规范（第三版，按“能不能看清 / 有没有中心”重排）：
///   · 顶部一条**贯穿全场**的暗带（BuildBand）当视觉锚点，六行的字都在这条带上，
///     位置永不变 —— 观众的眼睛只用看一个地方；暗带也为打字做了底衬。
///   · 每屏一个**主角**，尺寸拉开层级：1 屏 领土战争 > 立绘 > Deepseek；
///     2 屏 情报板 > AI 图；3 屏 道具槽 > 升级条 > 增益图标；
///     4 屏 示意板 > 炮塔；5 屏 护盾 > 穿甲；6 屏 扇形立绘。
///   · 标点拆句、原地换句：一句停留时间 = 整行时长 / 句数（打字限速 0.7 秒，其余全是阅读时间）。
///
/// 素材放 Assets/Resources/开场/（缺的先用占位播，Console 一条 Warning 列清单）：
///   Deepseek / 领土战争 / AI / 基地 / 穿甲 / 穿甲护盾 / 道具槽 / 锁 / 情报-*（六张）
/// 全部动画走舞台时钟（WaitStage / ClockDelta），不用 Time.deltaTime。
/// </summary>
public class OpeningRulesScene : StoryScene
{
    // —— 六行时长（用户定稿：7/9/5/13/5/9 秒）——
    private const float D1 = 7f;
    private const float D2 = 9f;
    private const float D3 = 5f;
    private const float D4 = 13f;
    private const float D5 = 5f;
    private const float D6 = 9f;

    // 开头 7 秒角落里的小字（要改空降时间就改这一处）
    private const string SkydiveNote = "【空降1:10跳过讲解】";

    // —— 文案带（整场不动）——
    private const float TextY = 2.62f;      // 文案带中心
    private const float TextW = 16.2f;      // 文案宽
    private const float TextH = 0.52f;      // 字高（世界单位）——上一版 0.37 太小
    private const float TextBoxH = 0.66f;   // 文案框高（一行够用；框越高 TMP 排版区越高、字越往上飘）
    private const float TextDrop = 0.05f;   // 垂直居中要补的差（先给一点点，嫌高/嫌低就改这一个数）
    private const float BandH = 1.6f;       // 顶部暗带高（留宽一点，字有点误差也不会“掉出带子”）
    private const float TextYBottom = -3.7f; // 最后一屏：暗带+字幕挪到画面最下方
    /// <summary>最后一屏的文案落点（画面最下方那条带）。</summary>
    private static Vector2 TextPosBottom => new Vector2(0f, TextYBottom - TextDrop);
    /// <summary>贯穿全场的那条暗带（最后一屏要把它挪到下方）。</summary>
    private static StoryTeller.Item _band;

    /// <summary>文案落点（暗带中心再往下 TextDrop）。</summary>
    private static Vector2 TextPos => new Vector2(0f, TextY - TextDrop);

    // —— 拆句节奏 ——
    private const float SayFade = 0.12f;   // 每句淡入/淡出
    private const float SayGap = 0.15f;    // 句与句之间的间隔：只留一口气，收掉就马上出下一句（要停顿是**屏间**那个 Gap）
    private const float SayMin = 1.1f;     // 每句最少停留（间隔太长时用它兜底）

    /// <summary>这行字实际要跑多久（含句间间隔）。比整行给定时长还长时，以实际时长为准。</summary>
    private static float SaySeconds(int count, float total)
    {
        if (count <= 0) return 0f;
        float per = Mathf.Max(SayMin, (total - 0.35f - (count - 1) * (SayFade + SayGap)) / count);
        return count * per + (count - 1) * (SayFade + SayGap);
    }

    /// <summary>
    /// 第 index 句（0 起）**开始显示**的时刻。和 PlayClauses 用同一套算法，
    /// 画面就照这个时间点排——念到哪句、哪一段动画才动，不会提前演完。
    /// </summary>
    private static float SayStart(int index, int count, float total)
    {
        if (count <= 0) return 0f;
        float per = Mathf.Max(SayMin, (total - 0.35f - (count - 1) * (SayFade + SayGap)) / count);
        return index * (per + SayFade + SayGap);
    }

    private static readonly Color Accent = StageStyle.Mark;
    /// <summary>三个增益里“没轮到”的那两张的网点密度：只淡一档，别弄成一团黑。</summary>
    private const float GainDim = 0.35f;
    /// <summary>第一屏摆好的立绘被后一张顶下去时的网点密度（最新的那张保持 1）。</summary>
    private const float PortraitDim = 0.35f;
    private static string Hex => ColorUtility.ToHtmlStringRGB(Accent);
    private static string Em(string s) { return "<color=#" + Hex + ">" + s + "</color>"; }
    private static Color Alpha(Color c, float a) { return new Color(c.r, c.g, c.b, a); }

    private const float InDur = 0.3f;
    private const float OutDist = 12f;
    private const float Gap = 0.1f;      // 内容滑走之后到下一屏开始之间的空档（尽量小，别留白屏）
    private const float HoldEnd = 0.7f;  // **一屏演完后，画面不动、原地停留 2 秒，然后才切下一屏**

    /// <summary>★ 临时预览开关：true = 第六屏（扇形立绘）先播一遍，方便先单独看这一屏；调好改回 false。</summary>
    private static readonly bool PreviewFanFirst = false;

    public override IEnumerator Play()
    {
        StoryTeller s = stage;
        if (s == null) yield break;
        Missing.Clear();

        // 贯穿整场的文案带：每屏换场时它不跟着走
        List<StoryTeller.Item> band = new List<StoryTeller.Item>();
        BuildBand(s, band);

        if (PreviewFanFirst)
        {
            yield return Beat6(s);
            if (_band != null)   // 预览完把暗带挪回顶部，后面的屏照常
            {
                yield return MovePlate(s, new List<StoryTeller.Item> { _band }, new Vector2(0f, TextY), 0.4f, false, null);
                yield return s.WaitStage(Gap);
            }
        }

        yield return Beat1(s);
        yield return Beat2(s);
        yield return Beat3(s);
        yield return Beat4(s);
        yield return Beat5(s);
        if (!PreviewFanFirst) yield return Beat6(s);

        yield return Out(s, band);
        ReportMissingArt();
    }

    #region 第一行：Deepseek + 领土战争（主角）+ 四立绘横扫（7s）
    private IEnumerator Beat1(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        // 大背景：领土战争原图（无底板、无描边）。模糊层排序是 0，所以这张图给 1 —— 才在它前面
        StoryTeller.Item bg = Frameless(Art("领土战争"), new Vector2(0f, -1.3f), new Vector2(15f, 8f));
        if (bg != null)
        {
            on.Add(bg);
            s.StartCoroutine(FadeTo(s, bg, 0f, 1f, 0.45f));
        }
        else
        {
            StoryTeller.Item t = Label(StageStyle.SizeTag(1.15f) + "<b>" + Em("领 土 战 争") + "</b></size>",
                new Vector2(0f, -1.0f), new Vector2(16f, 2f), Accent, StageStyle.FontSize(1.15f));
            if (t != null) { on.Add(t); s.StartCoroutine(s.SlideIn(t, StoryTeller.Direction.Botton, 5f, 0.34f, 0f, new Vector2(0.92f, 0.92f))); }
        }

        string l1 = "我把" + Em("Deepseek") + "——塞进了" + Em("领土战争") + "里，让他们能够控制炮塔的开火，并赋予了每个AI独立的人格。";
        StoryTeller.Item text = Narration(l1, TextPos, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l1, D1 - 0.5f)); }

        // Deepseek：先站**画面正中**（这一屏的主角），说到「人格」时再左移让位
        StoryTeller.Item hero = Frameless(Art("Deepseek"), new Vector2(0.2f, -1.5f), new Vector2(4.6f, 4.6f));
        if (hero != null)
        {
            hero.sortingOrder = 3;             // 压在领土战争背景之上
            ApplyAll(s, hero);
            on.Add(hero);
            s.StartCoroutine(s.SlideIn(hero, StoryTeller.Direction.Botton, 5f, 0.4f, 0f, new Vector2(0.9f, 0.9f)));
        }
        else
        {
            StoryTeller.Item ph = Label(StageStyle.SizeTag(0.5f) + Em("Deepseek") + "</size>",
                new Vector2(0.2f, -1.5f), new Vector2(3f, 0.7f), Accent, StageStyle.FontSize(0.5f));
            if (ph != null) { ph.sortingOrder = 3; ApplyAll(s, ph); on.Add(ph); s.StartCoroutine(s.SlideIn(ph, StoryTeller.Direction.Botton, 5f, 0.3f)); }
        }

        // 【空降00:00skip~~~】做成一块醒目的牌子，放在**文案带上方**
        StoryTeller.Item notePlate = Picture(null, new Vector2(0f, 4.15f), new Vector2(5.6f, 0.8f), Color.white, true);
        if (notePlate != null)
        {
            notePlate.cornerRadius = 0.2f;
            notePlate.containerColor = new Color(0.06f, 0.07f, 0.10f, 0.9f);
            notePlate.sortingOrder = 3;
            ApplyAll(s, notePlate);
            on.Add(notePlate);
            s.StartCoroutine(s.SlideIn(notePlate, StoryTeller.Direction.Top, 5f, 0.45f));
        }
        StoryTeller.Item note = Label(StageStyle.SizeTag(0.34f) + "<b>" + Em(SkydiveNote) + "</b></size>",
            new Vector2(0f, 4.15f), new Vector2(5.4f, 0.6f), Accent, StageStyle.FontSize(0.34f));
        if (note != null)
        {
            note.sortingOrder = 4;             // 文字 = 4+2 = 6，压住牌子
            ApplyAll(s, note);
            on.Add(note);
            s.StartCoroutine(s.SlideIn(note, StoryTeller.Direction.Top, 5f, 0.5f));
        }

        yield return s.WaitStage(0.5f); done += 0.5f;

        // 说到「并赋予了每个AI独立的人格」那一句时：Deepseek 左移，右边原地闪立绘（只淡入淡出）
        float shiftAt = SayStart(2, 3, D1 - 0.5f);
        if (done < shiftAt) { yield return s.WaitStage(shiftAt - done); done = shiftAt; }

        if (hero != null)
            s.StartCoroutine(MovePlate(s, new List<StoryTeller.Item> { hero }, new Vector2(-5.9f, -1.5f), 0.5f, false, new Vector2(0.82f, 0.82f)));

        // 四个立绘：**都从 A 点冒出来**，第1张滑到 D、第2张到 C、第3张到 B、第4张留在 A；
        // 每来一张，先前那几张就用**网点变暗**（最新的那张最亮）
        int stages = StageCount();
        float[] slotX = { 5.1f, 5.7f, 6.3f, 6.9f };      // A B C D（整体再往右）
        float[] slotTilt = { -7f, -3f, 3f, 7f };
        List<StoryTeller.Item> placed = new List<StoryTeller.Item>();
        for (int i = 1; i <= stages; i++)
        {
            int dst = Mathf.Clamp(stages - i, 0, slotX.Length - 1);   // 1→D 2→C 3→B 4→A
            StoryTeller.Item one = Portrait(i, SpriteEmotion.origin, new Vector2(slotX[0], -0.9f), new Vector2(5.0f, 5.9f), 1.12f, 0.3f);
            if (one == null) continue;
            one.position = new Vector2(slotX[0], -0.9f);
            one.size = new Vector2(5.0f, 5.9f);
            one.angle = slotTilt[dst];
            one.sortingOrder = 3 + i;
            ApplyAll(s, one);
            on.Add(one);

            // 在 A 点出现（亮）→ 滑到自己的位置
            s.StartCoroutine(s.SlideIn(one, StoryTeller.Direction.Botton, 3f, 0.18f, 0f, new Vector2(0.9f, 0.9f)));
            yield return s.WaitStage(0.22f); done += 0.22f;
            if (dst != 0)
            {
                yield return MovePlate(s, new List<StoryTeller.Item> { one }, new Vector2(slotX[dst], -0.9f), 0.3f, false, null);
                done += 0.3f;
            }

            // 先前摆好的那几张一律网点变暗；刚到的这张保持亮
            foreach (StoryTeller.Item old in placed) DotDim(old, true, PortraitDim);
            placed.Add(one);
            yield return s.WaitStage(0.08f); done += 0.08f;
        }

        // 文案因为句间 2 秒的间隔会比整行长，等它跑完（再留 0.4 秒）才收场
        float until1 = Mathf.Max(D1, SaySeconds(3, D1 - 0.5f)) + HoldEnd;
        if (done < until1) { yield return s.WaitStage(until1 - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 第二行：世界情报板（主角）连换（9s）
    private IEnumerator Beat2(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        // AI 在左（保留它自己的底板）：大一点、靠右一点，并往下挪开字幕带
        List<StoryTeller.Item> img3 = Plate("AI", "AI", null, new Vector2(-3.0f, -0.85f), 4.4f, Accent, on);
        SlideInPlate(s, img3, StoryTeller.Direction.Left, 7f, InDur, new Vector2(0.92f, 0.92f));

        string l2 = "AI每" + Em("7秒") + "会收到一个" + Em("世界情报") + "，包含其他AI说的话、地图情况、场上大球的位置，以及自己 弹药 道具 护盾的情况。";
        StoryTeller.Item text = Narration(l2, TextPos, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l2, D2 - 0.5f)); }

        // 右边：情报元素**直接铺在画面上**，没有那块黑色的圆角底板
        float bx = 2.6f;
        StoryTeller.Item header = Label(StageStyle.SizeTag(0.55f) + "<b>" + Em("世界情报") + "</b></size>",
            new Vector2(bx, 0.95f), new Vector2(9.6f, 0.75f), Accent, StageStyle.FontSize(0.55f));
        StoryTeller.Item cap = Label(StageStyle.SizeTag(0.42f) + "每 7 秒更新一次</size>",
            new Vector2(bx, 0.34f), new Vector2(9.6f, 0.6f), StageStyle.TextDim, StageStyle.FontSize(0.42f));
        if (header != null)
        {
            header.textOutlineWidth = 0.25f;
            header.textOutlineColor = new Color(0.03f, 0.04f, 0.06f, 1f);
            on.Add(header); ApplyAll(s, header); s.StartCoroutine(s.SlideIn(header, StoryTeller.Direction.Botton, 6f, 0.25f));
        }
        if (cap != null) { on.Add(cap); ApplyAll(s, cap); s.StartCoroutine(s.SlideIn(cap, StoryTeller.Direction.Botton, 5f, 0.28f)); }

        yield return s.WaitStage(0.5f); done += 0.5f;

        // 图片3 变换动作（每7秒那一拍）
        foreach (StoryTeller.Item it in img3) s.StartCoroutine(s.Pop(it, new Vector2(0.92f, 0.92f), Vector2.one, 0.3f));
        s.Float(img3.Count > 0 ? img3[0] : null, 0.07f, 2.8f);

        yield return s.WaitStage(0.6f); done += 0.6f;

        float cx = bx, cy = -1.3f;
        List<StoryTeller.Item> center = new List<StoryTeller.Item>();

        // 其他AI说的话
        StoryTeller.Item talker = Portrait(1, SpriteEmotion.origin, new Vector2(cx, cy), new Vector2(2.2f, 2.8f));
        List<StoryTeller.Item> talk = new List<StoryTeller.Item>();
        if (talker != null) talk.Add(talker);
        center = SwapCenter(s, center, CenterArt("情报-发言", talk, cx, cy));
        SetCat(s, cap, "其他AI说的话");
        yield return s.WaitStage(1.35f); done += 1.35f;

        // 地图情况：阵营色条
        List<StoryTeller.Item> map = new List<StoryTeller.Item>();
        int stages = StageCount();
        for (int i = 1; i <= stages; i++)
        {
            Color fc = FactionColor(i);
            StoryTeller.Item strip = AccentBar(Alpha(fc, 0.9f), new Vector2(cx + (i - (stages + 1) * 0.5f) * 1.15f, cy), 0.96f, 0.5f);
            if (strip != null) { ApplyAll(s, strip); map.Add(strip); }
        }
        center = SwapCenter(s, center, CenterArt("情报-地图", map, cx, cy));
        SetCat(s, cap, "地图情况");
        yield return s.WaitStage(1.35f); done += 1.35f;

        // 场上大球的位置：用游戏里的真球图标，直接铺在画面上
        center = SwapCenter(s, center, CenterArt("情报-大球", CenterVisual(Art("大球"), "大球", cx, cy, 3.0f), cx, cy));
        SetCat(s, cap, "场上大球的位置");
        yield return s.WaitStage(1.35f); done += 1.35f;

        // 弹药 / 道具 / 护盾 快速变换 3 次
        center = SwapCenter(s, center, CenterArt("情报-弹药", WordCenter("弹药", cx, cy), cx, cy));
        SetCat(s, cap, "弹药");
        yield return s.WaitStage(0.6f); done += 0.6f;

        center = SwapCenter(s, center, CenterArt("情报-道具", WordCenter("道具", cx, cy), cx, cy));
        SetCat(s, cap, "道具");
        yield return s.WaitStage(0.6f); done += 0.6f;

        List<StoryTeller.Item> shield = CenterVisual(s.iconShield, "护盾", cx, cy, 3.0f);
        center = SwapCenter(s, center, CenterArt("情报-护盾", shield, cx, cy));
        SetCat(s, cap, "护盾");
        yield return s.WaitStage(0.75f); done += 0.75f;

        if (center != null) on.AddRange(center);

        float until2 = Mathf.Max(D2, SaySeconds(3, D2 - 0.5f)) + HoldEnd;
        if (done < until2) { yield return s.WaitStage(until2 - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 第三行：道具槽（主角）+ 升级条（5s）
    private IEnumerator Beat3(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        string l3 = "每个空的道具槽每秒都会加一" + Em("升级点数") + "，升级时由AI选择各种增益。";
        StoryTeller.Item text = Narration(l3, TextPos, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l3, D3 - 0.5f)); }

        // 主角：道具槽（5 格，前 2 格空着会涨点）—— 整排往下挪，别贴字幕
        float slotY = 0.45f;
        float slotBox = 1.7f;
        int slotCount = 5;
        int openCount = 2;
        Sprite slotArt = Art("道具槽");
        Sprite lockArt = Art("锁");
        List<StoryTeller.Item> plusLabels = new List<StoryTeller.Item>();
        for (int i = 0; i < slotCount; i++)
        {
            float x = (i - (slotCount - 1) * 0.5f) * 2.02f;
            Vector2 p = new Vector2(x, slotY);
            bool open = i < openCount;
            List<StoryTeller.Item> slot = new List<StoryTeller.Item>();

            StoryTeller.Item panel = Picture(null, p, new Vector2(slotBox, slotBox), Color.white, true);
            if (panel != null)
            {
                panel.cornerRadius = slotBox * 0.2f;
                panel.containerColor = new Color(Accent.r, Accent.g, Accent.b, open ? 0.16f : 0.06f);
                panel.sortingOrder = 4;
                ApplyAll(s, panel); slot.Add(panel);
            }

            if (!open)
            {
                StoryTeller.Item lk = lockArt != null
                    ? Picture(lockArt, p, new Vector2(slotBox * 0.42f, slotBox * 0.42f), Alpha(Color.white, 0.55f))
                    : Label(StageStyle.SizeTag(0.34f) + "<color=#8A94A6>锁</color></size>", p, new Vector2(1.2f, 0.5f), StageStyle.TextDim, StageStyle.FontSize(0.34f));
                if (lk != null) { lk.sortingOrder = 6; ApplyAll(s, lk); slot.Add(lk); }
            }
            else
            {
                // 空槽里**常驻**一个 +1：每拍脉冲一下，不飞走（飞走了观众就看不见了）
                StoryTeller.Item plus = Label(StageStyle.SizeTag(0.6f) + "<b>" + Em("+1") + "</b></size>",
                    p, new Vector2(slotBox, 0.9f), Accent, StageStyle.FontSize(0.6f));
                if (plus != null)
                {
                    plus.sortingOrder = 6;
                    plus.textOutlineWidth = 0.3f;
                    plus.textOutlineColor = new Color(0.03f, 0.04f, 0.06f, 1f);
                    ApplyAll(s, plus); slot.Add(plus); plusLabels.Add(plus);
                }
            }

            on.AddRange(slot);
            SlideInPlate(s, slot, StoryTeller.Direction.Botton, 5f, 0.25f, new Vector2(0.9f, 0.9f));
        }

        // 升级条：空槽吐出来的点数往这里涨
        float barY = -1.85f;
        float fullW = 6.9f;
        StoryTeller.Item frame = Picture(null, new Vector2(0f, barY), new Vector2(7.4f, 0.95f), Color.white, true);
        if (frame != null)
        {
            StylePanel(frame, 0.3f, StagePanelBg);
            ApplyAll(s, frame); on.Add(frame);
            s.StartCoroutine(s.SlideIn(frame, StoryTeller.Direction.Botton, 6f, 0.3f));
        }
        StoryTeller.Item fill = AccentBar(Alpha(Accent, 0.95f), new Vector2(0f, barY), fullW, 0.62f);
        if (fill != null)
        {
            on.Add(fill);
            fill.SetScale(new Vector2(0.02f, 1f));
            fill.SetLocalPosition(new Vector2(-fullW * 0.5f, barY));
        }
        StoryTeller.Item counter = Label(StageStyle.SizeTag(0.46f) + "<b>" + Em("升级点数 0") + "</b></size>",
            new Vector2(0f, barY + 1.05f), new Vector2(5.4f, 0.66f), Accent, StageStyle.FontSize(0.46f));
        if (counter != null)
        {
            counter.sortingOrder = 4;
            counter.textOutlineWidth = 0.25f;
            counter.textOutlineColor = new Color(0.03f, 0.04f, 0.06f, 1f);
            ApplyAll(s, counter); on.Add(counter);
            s.StartCoroutine(s.SlideIn(counter, StoryTeller.Direction.Botton, 4f, 0.25f));
        }

        yield return s.WaitStage(0.55f); done += 0.55f;
        if (fill != null) fill.SetAlpha(1f);

        // 3 拍（每拍 0.8 秒）：空槽里的 +1 脉冲一下，条子涨一格
        float fillDur = 2.4f, ft = 0f;
        int ticks = 0;
        while (ft < fillDur)
        {
            yield return s.WaitForCaptureUpdate();
            ft += s.ClockDelta;
            float e = Mathf.Clamp01(ft / fillDur);
            e = e * e * (3f - 2f * e);
            if (fill != null)
            {
                fill.SetScale(new Vector2(Mathf.Max(0.02f, e), 1f));
                fill.SetLocalPosition(new Vector2(-fullW * 0.5f + fullW * 0.5f * e, barY));
            }

            int want = Mathf.Min(3, Mathf.FloorToInt(e * 3f));
            while (ticks < want)
            {
                ticks++;
                // 每拍是**每个空槽各加 1**：两个空槽就是 2、4、6，不是 1、2、3
                int shown = ticks * Mathf.Max(1, plusLabels.Count);
                SetTextHard(counter, StageStyle.SizeTag(0.46f) + "<b>" + Em("升级点数 " + shown) + "</b></size>");
                s.StartCoroutine(s.Pop(counter, new Vector2(1.22f, 1.22f), Vector2.one, 0.18f));
                foreach (StoryTeller.Item plus in plusLabels)
                    if (plus != null) s.StartCoroutine(s.Pop(plus, new Vector2(1.5f, 1.5f), Vector2.one, 0.4f));
            }
        }
        done += fillDur;
        if (fill != null) { fill.SetScale(Vector2.one); fill.SetLocalPosition(new Vector2(0f, barY)); }
        if (frame != null) s.StartCoroutine(s.Pop(frame, new Vector2(0.96f, 1.25f), Vector2.one, 0.25f));

        // 满：**等念到「升级时由AI选择各种增益」再出增益**（画面跟着字走，不再提前演完）
        float gainAt = SayStart(1, 2, D3 - 0.5f);
        if (done < gainAt) { yield return s.WaitStage(gainAt - done); done = gainAt; }
        foreach (StoryTeller.Item plus in plusLabels) s.StartCoroutine(FadeTo(s, plus, 1f, 0f, 0.25f));

        List<List<StoryTeller.Item>> gains = new List<List<StoryTeller.Item>>();
        for (int i = 1; i <= 3; i++)
        {
            Sprite ic = s.UpgradeIcon(i);
            if (ic == null) continue;
            float zoom = i == 2 ? 1.9f : 1.0f;      // 炮塔那张留白多，单独放大
            List<StoryTeller.Item> card = Plate(null, null, ic, new Vector2((i - 2) * 2.1f, -3.35f), 1.7f, Accent, null, zoom);
            gains.Add(card);
            on.AddRange(card);
        }
        foreach (List<StoryTeller.Item> g in gains)
            foreach (StoryTeller.Item it in g)
                if (it != null) s.StartCoroutine(FadeTo(s, it, 0f, 1f, 0.3f));

        yield return s.WaitStage(0.4f); done += 0.4f;

        // 依次扫两轮：轮到的那张**亮起来并弹一下**，其余只是**轻微**网点变稀（不弄黑），最后停在第 3 个
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < gains.Count; i++)
            {
                for (int j = 0; j < gains.Count; j++)
                {
                    foreach (StoryTeller.Item it in gains[j])
                        if (it != null) DotDim(it, j != i, GainDim);
                }
                if (gains[i].Count > 0)
                    s.StartCoroutine(s.Pop(gains[i][0], new Vector2(1.15f, 1.15f), Vector2.one, 0.2f));
                yield return s.WaitStage(0.18f); done += 0.18f;
            }
        }
        int pick = gains.Count - 1;
        for (int j = 0; j < gains.Count; j++)
            foreach (StoryTeller.Item it in gains[j])
                if (it != null) DotDim(it, j != pick, GainDim);

        float until3 = Mathf.Max(D3, SaySeconds(2, D3 - 0.5f)) + HoldEnd;
        if (done < until3) { yield return s.WaitStage(until3 - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 第四行：黑暗森林 + 移动基地（13s）
    private IEnumerator Beat4(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        string l4 = "为了让游戏更有意思在本场对局里，AI是" + Em("不知道其他AI的位置的") + "，只能通过子弹或者大球" + Em("命中来推测位置") + "。并且AI可以通过" + Em("消耗升级点数来移动基地") + "。";
        StoryTeller.Item text = Narration(l4, TextPos, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l4, D4 - 0.5f)); }

        // 图跟字对齐：三句的时间点直接取文字的时间表（第2句摆炮塔、第3句命中、第4句移动）
        float t2 = SayStart(1, 4, D4 - 0.5f);
        float t3 = SayStart(2, 4, D4 - 0.5f);
        float t4 = SayStart(3, 4, D4 - 0.5f);

        // 主角：整块示意板（板子加高，演示内容居中，不再全挤在板子底部）
        StoryTeller.Item board = Picture(null, new Vector2(0f, -1.4f), new Vector2(13.8f, 5.4f), Color.white, true);
        if (board != null)
        {
            StylePanel(board, 0.32f, StagePanelBg);
            ApplyAll(s, board); on.Add(board);
            s.StartCoroutine(s.SlideIn(board, StoryTeller.Direction.Botton, 7f, 0.32f));
        }

        yield return s.WaitStage(0.5f); done += 0.5f;

        // 第1句只是铺垫：等它念完再摆东西
        yield return s.WaitStage(Mathf.Max(0.1f, t2 - done)); done = t2;

        // 第2句（AI 是不知道其他 AI 的位置的）：自己的炮塔（阵营色）+ 敌方炮塔（阵营色 + "?"）
        List<StoryTeller.Item> phaseA = new List<StoryTeller.Item>();
        Color mineColor = FactionColor(1);
        Color foeColor = FactionColor(2);
        List<StoryTeller.Item> turretA = Plate(null, null, s.iconTurret, new Vector2(-4.2f, -1.8f), 2.0f, Accent, phaseA, 1.9f);
        if (turretA.Count > 1) Tint(turretA[1], mineColor);
        List<StoryTeller.Item> turretB = Plate(null, null, s.iconTurret, new Vector2(4.2f, -0.7f), 2.0f, Accent, phaseA, 1.9f);
        if (turretB.Count > 1) Tint(turretB[1], foeColor);
        StoryTeller.Item qB = Label(StageStyle.SizeTag(0.8f) + Em("?") + "</size>", new Vector2(4.2f, 0.95f), new Vector2(1.2f, 0.9f), Accent, StageStyle.FontSize(0.8f));
        if (qB != null) { ApplyAll(s, qB); phaseA.Add(qB); }
        SlideInPlate(s, turretA, StoryTeller.Direction.Botton, 3f, 0.22f, new Vector2(0.9f, 0.9f));
        SlideInPlate(s, turretB, StoryTeller.Direction.Botton, 3f, 0.22f, new Vector2(0.9f, 0.9f));
        s.StartCoroutine(s.SlideIn(qB, StoryTeller.Direction.Botton, 3f, 0.22f, 0f, new Vector2(0.9f, 0.9f)));

        yield return s.WaitStage(0.5f); done += 0.5f;
        // 双方在对方眼里位置都是未知的 → **两座都压暗**；
        // 开火的那座不会因为开火而变亮，只有**被命中、被发现**的那座才亮起来
        foreach (StoryTeller.Item it in turretA) DotDim(it, true);
        foreach (StoryTeller.Item it in turretB) DotDim(it, true);

        // 第3句（只能通过子弹或者大球命中来推测位置）：
        // **先撞**——大球提前 0.7 秒起飞，好让「撞上」正好落在这一句开始的那一刻
        float hitAt = t3;
        yield return s.WaitStage(Mathf.Max(0.1f, hitAt - 0.7f - done)); done = hitAt - 0.7f;

        List<StoryTeller.Item> ball = BallGroup(new Vector2(-4.2f, -1.8f), mineColor);
        Vector2 hitPos = new Vector2(3.15f, -0.7f);          // 撞在蓝色炮塔**左边缘**上（不是压在它身上）
        yield return MovePlate(s, ball, hitPos, 1.4f, false, null);
        done = hitAt + 0.7f;

        // 撞上 → 停一下 → **反弹回来并且不消失**；这一小段动画播完，才接着演“发现”
        yield return s.WaitStage(0.1f); done += 0.1f;
        yield return MovePlate(s, ball, hitPos + new Vector2(-1.0f, -0.15f), 0.26f, false, null);
        done += 0.26f;

        // 反弹停稳 → 这才“发现”：那座亮起来、"?" → "!"，标出推测位置
        foreach (StoryTeller.Item it in turretB) { DotDim(it, false); s.StartCoroutine(s.Pop(it, new Vector2(0.9f, 0.9f), Vector2.one, 0.3f)); }
        if (qB != null) { SetTextHard(qB, StageStyle.SizeTag(0.8f) + Em("!") + "</size>"); s.StartCoroutine(s.Pop(qB, new Vector2(1.35f, 1.35f), Vector2.one, 0.25f)); }
        StoryTeller.Item r1 = AccentBar(Alpha(Accent, 0.95f), new Vector2(4.2f, -0.7f), 1.3f, 0.1f);
        StoryTeller.Item r2 = AccentBar(Alpha(Accent, 0.95f), new Vector2(4.2f, -0.7f), 1.3f, 0.1f);
        if (r1 != null) { r1.angle = 45f; Lit(r1); phaseA.Add(r1); s.StartCoroutine(s.Pop(r1, new Vector2(0.4f, 0.4f), Vector2.one, 0.3f)); }
        if (r2 != null) { r2.angle = -45f; Lit(r2); phaseA.Add(r2); s.StartCoroutine(s.Pop(r2, new Vector2(0.4f, 0.4f), Vector2.one, 0.3f)); }
        StoryTeller.Item guess = Label(StageStyle.SizeTag(0.5f) + Em("推测位置"), new Vector2(0.9f, -2.4f), new Vector2(3.6f, 0.7f), Accent, StageStyle.FontSize(0.5f));
        if (guess != null) { ApplyAll(s, guess); phaseA.Add(guess); s.StartCoroutine(s.SlideIn(guess, StoryTeller.Direction.Right, 3f, 0.22f, 0f, new Vector2(0.92f, 0.92f))); }

        yield return s.WaitStage(0.35f); done += 0.35f;

        // 知道它在哪了，自己的炮塔**这才转过去对准它**（图里炮口默认朝上，转到指向右上方那座）
        float aim = -90f + Mathf.Atan2(1.1f, 8.4f) * Mathf.Rad2Deg;
        foreach (StoryTeller.Item it in turretA) s.StartCoroutine(TiltTo(s, it, 0f, aim, 0.45f));
        yield return s.WaitStage(0.5f); done += 0.5f;

        // 球命中后留在原地弹一下，跟着这一组一起退场（不再淡掉，不然只出现一瞬间）
        phaseA.AddRange(ball);
        foreach (StoryTeller.Item it in ball) s.StartCoroutine(s.Pop(it, new Vector2(1.18f, 1.18f), Vector2.one, 0.3f));

        // 第4句（消耗升级点数移动基地）：先清掉上面那组，再演移动
        yield return s.WaitStage(Mathf.Max(0.1f, t4 - done)); done = t4;

        foreach (StoryTeller.Item it in phaseA) s.StartCoroutine(s.SlideOut(it, StoryTeller.Direction.Botton, 4f, 0.2f));
        yield return s.WaitStage(0.25f); done += 0.25f;

        Sprite baseArt = Art("基地");
        Sprite baseIcon = baseArt != null ? baseArt : s.iconTurret;
        List<StoryTeller.Item> live = Plate(null, null, baseIcon, new Vector2(-4.0f, -1.8f), 2.0f, Accent, on, baseArt != null ? 1f : 1.9f);
        StoryTeller.Item o1 = AccentBar(Alpha(Accent, 0.7f), new Vector2(-4.0f, -1.8f), 1.2f, 0.08f);
        StoryTeller.Item o2 = AccentBar(Alpha(Accent, 0.7f), new Vector2(-4.0f, -1.8f), 1.2f, 0.08f);
        if (o1 != null) { o1.angle = 45f; ApplyAll(s, o1); on.Add(o1); }
        if (o2 != null) { o2.angle = -45f; ApplyAll(s, o2); on.Add(o2); }
        StoryTeller.Item baseLabel = Label(StageStyle.SizeTag(0.42f) + "<color=#8A94A6>基地</color></size>", new Vector2(-4.0f, -2.95f), new Vector2(2.2f, 0.6f), StageStyle.TextDim, StageStyle.FontSize(0.42f));
        if (baseLabel != null) { ApplyAll(s, baseLabel); on.Add(baseLabel); }
        for (int i = 0; i < 3; i++)
        {
            StoryTeller.Item slash = AccentBar(Alpha(Accent, 0.9f), new Vector2(-2.1f + i * 1.4f, -1.8f), 0.85f, 0.12f);
            if (slash != null) { slash.angle = 45f; Lit(slash); on.Add(slash); }
        }

        float cost = 50f;
        if (MapConfig.Instance != null) cost = MapConfig.Instance.moveEnergyCost;
        StoryTeller.Item badge = Label(StageStyle.SizeTag(0.6f) + "<b>" + Em("消耗" + cost.ToString("0.#")) + "</b></size>", new Vector2(0f, 0.3f), new Vector2(5f, 0.8f), Accent, StageStyle.FontSize(0.6f));
        if (badge != null)
        {
            badge.textOutlineWidth = 0.25f;
            badge.textOutlineColor = new Color(0.03f, 0.04f, 0.06f, 1f);
            ApplyAll(s, badge); on.Add(badge);
        }

        SlideInPlate(s, live, StoryTeller.Direction.Left, 4f, 0.22f, new Vector2(0.9f, 0.9f));
        s.StartCoroutine(s.SlideIn(o1, StoryTeller.Direction.Botton, 3f, 0.2f, 0f, new Vector2(0.4f, 0.4f)));
        s.StartCoroutine(s.SlideIn(o2, StoryTeller.Direction.Botton, 3f, 0.2f, 0f, new Vector2(0.4f, 0.4f)));
        if (baseLabel != null) s.StartCoroutine(s.SlideIn(baseLabel, StoryTeller.Direction.Botton, 3.5f, 0.22f));
        if (badge != null) s.StartCoroutine(s.SlideIn(badge, StoryTeller.Direction.Top, 3.5f, 0.25f, 0f, new Vector2(0.9f, 0.9f)));

        // 先把「消耗25」亮出来让人看清（弹一下），再开始移动
        yield return s.WaitStage(1.0f); done += 1.0f;
        if (badge != null) s.StartCoroutine(s.Pop(badge, new Vector2(1.25f, 1.25f), Vector2.one, 0.3f));
        yield return s.WaitStage(0.3f); done += 0.3f;

        // 移动：左 → 右（消耗升级点数）
        yield return MovePlate(s, live, new Vector2(4.0f, -1.8f), 1.6f, false, null);
        done += 1.6f;
        foreach (StoryTeller.Item it in live) { s.StartCoroutine(s.Pop(it, new Vector2(1.08f, 1.08f), Vector2.one, 0.25f)); s.Float(it, 0.05f, 3.8f); }

        float until4 = Mathf.Max(D4, SaySeconds(4, D4 - 0.5f)) + HoldEnd;
        if (done < Mathf.Max(until4, t4 + 0.25f)) { yield return s.WaitStage(Mathf.Max(until4, t4 + 0.25f) - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 第五行：穿甲钻入护盾（5s）
    private IEnumerator Beat5(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        string l5 = "另外相比上期我新增了一个道具：" + Em("穿甲") + "，能够直接" + Em("钻入护盾") + "。";
        StoryTeller.Item text = Narration(l5, TextPos, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l5, D5 - 0.5f)); }

        // 图6：穿甲（从左边冲进来）；图7：小鱼（AI）外面套一层炮塔上的护盾
        List<StoryTeller.Item> pierce = Plate("穿甲", "穿甲", null, new Vector2(-7.8f, -0.5f), 3.0f, Accent, null);
        SlideInPlate(s, pierce, StoryTeller.Direction.Left, 5f, 0.25f, new Vector2(0.94f, 0.94f));

        List<StoryTeller.Item> pet = new List<StoryTeller.Item>();
        // 小鱼（AI 图）排在上面：护盾只在它外面当一圈，不会把里面的人盖掉
        Sprite petShieldArt = Art("护盾");                                  // = Towel.prefab 上 ShieldPic 用的那张（pic/UI/shieldWave.png）
        if (petShieldArt == null) petShieldArt = s.iconShield;
        StoryTeller.Item shield = petShieldArt != null ? Frameless(petShieldArt, new Vector2(3.4f, -0.6f), new Vector2(5.6f, 5.6f)) : null;
        if (shield != null)
        {
            shield.sortingOrder = 4;
            ApplyAll(s, shield);
            pet.Add(shield); on.Add(shield);
            s.StartCoroutine(s.SlideIn(shield, StoryTeller.Direction.Right, 7f, InDur, 0f, new Vector2(0.92f, 0.92f)));
        }
        StoryTeller.Item petArt = Frameless(Art("穿甲护盾") != null ? Art("穿甲护盾") : Art("AI"), new Vector2(3.4f, -0.6f), new Vector2(3.4f, 3.4f));
        if (petArt != null)
        {
            petArt.sortingOrder = 5;
            ApplyAll(s, petArt);
            pet.Add(petArt); on.Add(petArt);
            s.StartCoroutine(s.SlideIn(petArt, StoryTeller.Direction.Right, 7f, InDur, 0f, new Vector2(0.92f, 0.92f)));
        }

        yield return s.WaitStage(0.55f); done += 0.55f;

        // 等念到「能够直接钻入护盾」那一句再飞（画面跟着字走）
        float zipAt = SayStart(1, 2, D5 - 0.5f);
        if (done < zipAt) { yield return s.WaitStage(zipAt - done); done = zipAt; }

        // 窜过去：穿甲高速横穿画面，路过护盾时把护盾顶一下 —— **不淡出、不被吃掉**，直接窜出画外
        foreach (StoryTeller.Item it in pierce) s.StartCoroutine(TiltTo(s, it, 0f, 20f, 0.6f));
        yield return ZipThrough(s, pierce, pet, new Vector2(9.0f, -0.5f), 0.9f);
        done += 0.9f;

        float until5 = Mathf.Max(D5, SaySeconds(2, D5 - 0.5f)) + HoldEnd;
        if (done < until5) { yield return s.WaitStage(until5 - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 第六行：立绘扇形摊开 → 消失（9s）
    private IEnumerator Beat6(StoryTeller s)
    {
        List<StoryTeller.Item> on = new List<StoryTeller.Item>();
        float done = 0f;

        string[] l6Clauses =
        {
            "那么AI会怎么样在这个" + Em("黑暗森林") + "里",
            "分析、思考、",
            "甚至是结盟、猜忌，背刺？",
            "让我们拭目以待。"
        };
        string l6 = string.Concat(l6Clauses);
        StoryTeller.Item text = Narration(l6, TextPosBottom, new Vector2(TextW, TextBoxH), TextH);
        if (text != null) { on.Add(text); s.StartCoroutine(PlayClauses(s, text, l6Clauses, D6 - 0.5f)); }

        yield return s.WaitStage(0.5f); done += 0.5f;

        // 最后一屏：暗带+字幕**挪到画面最下方**，立绘在它上面放大排开
        if (_band != null)
        {
            yield return MovePlate(s, new List<StoryTeller.Item> { _band }, new Vector2(0f, TextYBottom), 0.5f, false, null);
            done += 0.5f;
        }

        // 主角：立绘扇形摊开 —— 拱桥形、朝外开、更大，整体落在上半部
        int stages = StageCount();
        float[] xs = { -2.4f, -0.8f, 0.8f, 2.4f };
        float[] ys = { 0.0f, 1.1f, 1.1f, 0.0f };      // 比上一版整体下来 0.5（大小不变）
        float[] angs = { 25f, 8.5f, -8.5f, -25f };      // 左边那张往左倒、右边那张往右倒（扇形朝外开）
        List<StoryTeller.Item> fan = new List<StoryTeller.Item>();
        for (int i = 1; i <= stages; i++)
        {
            int k = (i - 1) % 4;
            StoryTeller.Item card = Portrait(i, SpriteEmotion.origin, new Vector2(xs[k], ys[k]), new Vector2(5.4f, 6.8f), 1.1f, 0.3f);
            if (card == null) continue;
            card.position = new Vector2(xs[k], ys[k]);
            card.size = new Vector2(5.4f, 6.8f);
            card.angle = angs[k];
            ApplyAll(s, card);
            fan.Add(card);
            s.StartCoroutine(TiltTo(s, card, angs[k] + (k < 2 ? 26f : -26f), angs[k], 0.55f));
            s.StartCoroutine(s.SlideIn(card, StoryTeller.Direction.Botton, 9f, 0.36f, 0f, new Vector2(0.86f, 0.86f)));
            yield return s.WaitStage(0.11f); done += 0.11f;
        }
        yield return s.WaitStage(0.5f); done += 0.5f;
        foreach (StoryTeller.Item card in fan)
            if (card != null) s.Float(card, 0.06f, 4.5f);

        // 读完问题 → 「让我们拭目以待」那一句开始：立绘集体消失
        float vanishAt = SayStart(3, 4, D6 - 0.5f);
        if (done < vanishAt) { yield return s.WaitStage(vanishAt - done); done = vanishAt; }
        foreach (StoryTeller.Item card in fan) s.StartCoroutine(s.SlideOut(card, StoryTeller.Direction.Top, 9f, 0.3f));
        yield return s.WaitStage(0.35f); done += 0.35f;

        float until6 = Mathf.Max(D6, SaySeconds(4, D6 - 0.5f)) + HoldEnd;
        if (done < until6) { yield return s.WaitStage(until6 - done); }
        yield return Out(s, on);
    }
    #endregion

    #region 公用件

    /// <summary>整场都在的顶部暗带：六行的字都落在这一条上，位置永不变，也顺便给字做底衬。</summary>
    private static void BuildBand(StoryTeller s, List<StoryTeller.Item> band)
    {
        StoryTeller.Item b = Picture(null, new Vector2(0f, TextY), new Vector2(19f, BandH), Color.white, true);
        if (b == null) return;
        b.cornerRadius = 0f;
        b.containerColor = StagePanelBg;
        b.wallColor = new Color(0f, 0f, 0f, 0f);
        b.wallWidth = 0f;
        b.sortingOrder = 2;   // 战场模糊层是 0：图片 1、暗带 2、文字 3+，这样才看得见
        s.ApplyItem(b);
        _band = b;
        band.Add(b);
        s.StartCoroutine(s.SlideIn(b, StoryTeller.Direction.Top, 4f, 0.35f, 0f, new Vector2(0.96f, 0.96f)));
    }

    private static readonly List<string> Missing = new List<string>();
    private static Sprite Art(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        Sprite sp = Resources.Load<Sprite>("开场/" + name);
        if (sp == null && !Missing.Contains(name)) Missing.Add(name);
        return sp;
    }
    private static void ReportMissingArt()
    {
        if (Missing.Count == 0) return;
        Debug.LogWarning("[开场介绍] 缺素材（先用占位播）：Assets/Resources/开场/ 需要："
            + string.Join("、", Missing.ToArray())
            + "；每个名字一张 png，如 开场/Deepseek.png。");
    }

    private static int StageCount()
    {
        MapConfig cfg = MapConfig.Instance;
        if (cfg != null && cfg.teamColors != null && cfg.teamColors.Count > 1)
            return cfg.teamColors.Count - 1;
        return 4;
    }
    private static Color FactionColor(int stage)
    {
        MapConfig cfg = MapConfig.Instance;
        if (cfg != null) return cfg.GetColor(stage, MapConfig.ColorStage.Towel);
        return Accent;
    }

    /// <summary>文案统一色：中文正文用近白，关键处用 Em() 染成强调色。</summary>
    private static StoryTeller.Item Narration(string rich, Vector2 pos, Vector2 size, float h)
    {
        return Label(StageStyle.SizeTag(h) + rich + "</size>", pos, size, StageStyle.TextMain, StageStyle.FontSize(h));
    }

    /// <summary>按逗号拆句（，。！？；都算句末），顿号「、」不算 —— 它只是并列停顿。</summary>
    private static string[] Clauses(string full)
    {
        List<string> parts = new List<string>();
        if (string.IsNullOrEmpty(full)) return parts.ToArray();
        int start = 0;
        for (int i = 0; i < full.Length; i++)
        {
            char c = full[i];
            if (c == '，' || c == '。' || c == '！' || c == '？' || c == '；'
                || c == ',' || c == '.' || c == '!' || c == '?' || c == ';')
            {
                string t = full.Substring(start, i - start + 1).Trim();
                if (t.Length > 0) parts.Add(t);
                start = i + 1;
            }
        }
        if (start < full.Length)
        {
            string t = full.Substring(start).Trim();
            if (t.Length > 0) parts.Add(t);
        }
        return parts.ToArray();
    }

    /// <summary>
    /// 一行字按句拆开，在**同一个位置**一句接一句：这一句显示完就淡出，下一句原地淡入。
    /// 只用透明度、**不移动、不缩放、也不逐字打**——逐字入场那套会给整行叠一个起始位移，
    /// 打完不立刻归位就会看着“飘”；这里直接整句出，位置绝对不漂，时间也全省下来给阅读。
    /// </summary>
    private static IEnumerator PlayClauses(StoryTeller s, StoryTeller.Item line, string full, float total)
    {
        return PlayClauses(s, line, Clauses(full), total);
    }

    /// <summary>想自己定断句就传这个重载（比如最后一行：那么~里 ｜ 分析、思考、 ｜ 甚至~背刺 ｜ 让我们拭目以待）。</summary>
    private static IEnumerator PlayClauses(StoryTeller s, StoryTeller.Item line, string[] clauses, float total)
    {
        if (s == null || line == null || clauses == null || clauses.Length == 0) yield break;

        const float fade = SayFade;   // 淡入/淡出
        const float gap = SayGap;     // 一句收掉之后，停 2 秒再出下一句

        // 每句时长里先把「淡入淡出 + 间隔」扣掉，剩下的才是停留阅读时间
        float per = Mathf.Max(SayMin, (total - 0.35f - (clauses.Length - 1) * (fade + gap)) / clauses.Length);
        if (line.textDisplay != null) line.textDisplay.typeMaxSeconds = 0.3f;

        for (int i = 0; i < clauses.Length; i++)
        {
            SetTextHard(line, StageStyle.SizeTag(TextH) + clauses[i] + "</size>");
            yield return FadeTo(s, line, 0f, 1f, fade);
            yield return s.WaitStage(Mathf.Max(0f, per - fade));
            if (i < clauses.Length - 1)
            {
                yield return FadeTo(s, line, 1f, 0f, fade);
                yield return s.WaitStage(gap);      // 空一拍：这句收干净了，下一句再出来
            }
        }
    }

    private static void ApplyAll(StoryTeller s, StoryTeller.Item it)
    {
        if (it != null && s != null) s.ApplyItem(it);
    }

    /// <summary>原始图片：不套底板、不加描边（“就是那张图”），并换正常 sprite 材质免得被网点压暗。</summary>
    private static StoryTeller.Item Frameless(Sprite art, Vector2 pos, Vector2 frame)
    {
        if (art == null) return null;
        StoryTeller.Item it = Picture(art, pos, frame, Color.white, false, 1f);
        if (it == null) return null;
        it.sortingOrder = 1;
        ApplyAll(stage, it);
        Brighten(it);
        return it;
    }

    /// <summary>
    /// 横穿：把一组图从当前位置**高速窜到目标点**（不淡出、不缩小），
    /// 路过 target 那一组时让它弹一下（表示“穿甲隔着护盾照样过去”），窜出画外后销毁。
    /// </summary>
    private static IEnumerator ZipThrough(StoryTeller s, List<StoryTeller.Item> flyer, List<StoryTeller.Item> target, Vector2 to, float dur)
    {
        if (s == null || flyer == null || flyer.Count == 0) yield break;
        StoryTeller.Item anchor = flyer[0];
        if (anchor == null || anchor.realOb == null) yield break;

        float hitX = target != null && target.Count > 0 && target[0] != null ? target[0].position.x : float.MaxValue;
        float fromX = anchor.position.x;
        bool hit = false;
        float t = 0f;
        while (t < dur)
        {
            yield return s.WaitForCaptureUpdate();
            t += s.ClockDelta;
            float k = Mathf.Clamp01(t / dur);
            float e = k * k * (3f - 2f * k);
            float x = Mathf.Lerp(fromX, to.x, e);
            foreach (StoryTeller.Item it in flyer)
            {
                if (it == null || it.realOb == null) continue;
                it.SetLocalPosition(new Vector2(x, it.position.y));
            }
            if (!hit && x >= hitX)
            {
                hit = true;
                if (target != null)
                    foreach (StoryTeller.Item p in target)
                        if (p != null) s.StartCoroutine(s.Pop(p, new Vector2(1.16f, 1.16f), Vector2.one, 0.32f));
            }
        }
        foreach (StoryTeller.Item it in flyer)
            if (it != null) it.Delete();
    }

    /// <summary>
    /// 压暗一律走**网点**（dot）：改 spriteVisibility，网点越稀越暗 —— 和舞台立绘 DimVisibility 一个口径，
    /// 不用整体透明度（半透明会透出背景，看着发灰）。没有 sprite 的（纯底板/文字）才退回透明度。
    /// </summary>
    private static void DotDim(StoryTeller.Item item, bool dim, float level = -1f)
    {
        if (item == null) return;
        float v = dim ? (level > 0f ? level : StoryScene.DimVisibility) : 1f;
        Material dot = Resources.Load<Material>("Material/SpriteMeshDot");
        if (item.spriteDisplay != null && item.sprite != null && dot != null)
        {
            // 走网点材质的 _Alpha（“整体可见度 = 可见面积比例”）：网点变稀就是变暗
            // 材质不是网点时 visibility 不起作用，所以这里**强制换成网点材质**
            if (item.spriteDisplay.material != dot) item.spriteDisplay.SetMaterial(dot);
            item.spriteVisibility = v;
            item.spriteDisplay.visibility = v;
            item.spriteDisplay.Rebuild();
            item.SetAlpha(1f);
        }
        else
        {
            item.SetAlpha(dim ? 0.5f : 1f);   // 纯底板/文字没有网点，只能压透明度
        }
    }

    /// <summary>
    /// 建出来就**立刻点亮**的小块（不走进场动画的那种：大球、准星叉、轨迹条…）。
    /// StoryTeller 生成卡时一律先 alpha=0 藏着等入场动画，不显式点亮就会全程隐形，
    /// 直到退场 SlideOut 把 alpha 从 1 往下淡的那一下才闪出来。
    /// </summary>
    private static StoryTeller.Item Lit(StoryTeller.Item it)
    {
        if (it == null) return null;
        it.SetAlpha(1f);
        ApplyAll(stage, it);
        return it;
    }

    /// <summary>给一块图卡染上阵营色（炮塔 / 大球用）。</summary>
    private static void Tint(StoryTeller.Item item, Color c)
    {
        if (item == null) return;
        item.textColor = c;
        ApplyAll(stage, item);
    }

    /// <summary>大球：球图标 + 里面写上「大球」。颜色 = 开火方阵营色**往白里提一档**（深色阵营不然在黑底上看不见）。</summary>
    private static List<StoryTeller.Item> BallGroup(Vector2 pos, Color color)
    {
        List<StoryTeller.Item> made = new List<StoryTeller.Item>();
        Sprite art = Art("大球");
        Color bright = Color.Lerp(color, Color.white, 0.45f);
        const float box = 2.4f;                      // 原来 1.5：太小，画面上一晃就没了

        StoryTeller.Item ball = art != null ? Frameless(art, pos, new Vector2(box, box)) : null;
        if (ball != null)
        {
            ball.sortingOrder = 6;
            Tint(ball, bright);
        }        else
        {
            ball = Picture(null, pos, new Vector2(box, box), Color.white, true);
            if (ball != null)
            {
                ball.cornerRadius = box * 0.5f;
                ball.containerColor = bright;
                ball.sortingOrder = 6;
                ApplyAll(stage, ball);
            }
        }
        Lit(ball);
        if (ball != null) made.Add(ball);

        StoryTeller.Item word = Label(StageStyle.SizeTag(0.44f) + "<b>大球</b></size>", pos, new Vector2(2.2f, 0.7f),
            new Color(0.10f, 0.12f, 0.16f), StageStyle.FontSize(0.44f));
        if (word != null) { word.sortingOrder = 8; Lit(word); made.Add(word); }
        return made;
    }

    /// <summary>只改透明度（不移动、不缩放、不旋转）。</summary>
    private static IEnumerator FadeTo(StoryTeller s, StoryTeller.Item item, float from, float to, float dur)
    {
        if (s == null || item == null || item.realOb == null) yield break;
        if (dur <= 0f) { item.SetAlpha(to); yield break; }
        float t = 0f;
        while (t < dur)
        {
            yield return s.WaitForCaptureUpdate();
            t += s.ClockDelta;
            float k = Mathf.Clamp01(t / dur);
            k = k * k * (3f - 2f * k);
            item.SetAlpha(Mathf.Lerp(from, to, k));
        }
        item.SetAlpha(to);
    }

    /// <summary>把一块图片换成正常 sprite 材质：网点材质（SpriteMeshDot）本身会把图压暗，主角图换掉更亮。</summary>
    private static void Brighten(StoryTeller.Item item)
    {
        if (item == null || item.spriteDisplay == null) return;
        Material mat = Resources.Load<Material>("Material/SpriteMeshDefault");
        if (mat != null) item.spriteDisplay.SetMaterial(mat);
    }

    /// <summary>底板只改圆角与底色（**底色保持原样，不做任何改动**）。</summary>
    private static void StylePanel(StoryTeller.Item item, float corner, Color fill)
    {
        if (item == null) return;
        item.cornerRadius = corner;
        item.containerColor = fill;
        item.wallWidth = StageStyle.Wall;
        item.sortingOrder = 1;
        stage.ApplyItem(item);
    }

    /// <summary>面板底色（跟原来一致，不改）。</summary>
    private static Color StagePanelBg => StageStyle.PanelBg;

    /// <summary>
    /// 圆角底板 + 图标/占位文字。三优先级：指定素材 > 传入图标 > 占位词。
    /// zoom 用来补正素材本身留白造成的「看起来小」（炮塔那张留白最多）。
    /// </summary>
    private static List<StoryTeller.Item> Plate(string artName, string fallbackWord, Sprite icon, Vector2 pos, float box, Color accent, List<StoryTeller.Item> bag, float zoom = 1f)
    {
        StoryTeller s = stage;
        List<StoryTeller.Item> pieces = new List<StoryTeller.Item>();
        if (s == null) return pieces;

        Sprite inner = Art(artName);
        if (inner == null) inner = icon;

        StoryTeller.Item panel = Picture(null, pos, new Vector2(box, box), Color.white, true);
        if (panel != null)
        {
            StylePanel(panel, box * 0.2f, new Color(accent.r, accent.g, accent.b, 0.13f));
            ApplyAll(s, panel);
            pieces.Add(panel);
        }
        if (inner != null)
        {
            StoryTeller.Item pic = Picture(inner, pos, new Vector2(box * 0.86f, box * 0.86f), Color.white, false, zoom);
            if (pic != null) { pic.sortingOrder = 2; ApplyAll(s, pic); pieces.Add(pic); }
        }
        else if (!string.IsNullOrEmpty(fallbackWord))
        {
            StoryTeller.Item word = Label(StageStyle.SizeTag(0.4f) + Em(fallbackWord) + "</size>", pos, new Vector2(box * 0.95f, 0.8f), accent, StageStyle.FontSize(0.4f));
            if (word != null) { word.sortingOrder = 2; ApplyAll(s, word); pieces.Add(word); }
        }
        if (bag != null) bag.AddRange(pieces);
        return pieces;
    }

    private static List<StoryTeller.Item> WordCenter(string word, float x, float y)
    {
        List<StoryTeller.Item> pieces = new List<StoryTeller.Item>();
        StoryTeller.Item t = Label(StageStyle.SizeTag(0.95f) + "<b>" + Em(word) + "</b></size>", new Vector2(x, y), new Vector2(4.4f, 1.3f), Accent, StageStyle.FontSize(0.95f));
        if (t != null)
        {
            t.sortingOrder = 4;
            t.textOutlineWidth = 0.25f;
            t.textOutlineColor = new Color(0.03f, 0.04f, 0.06f, 1f);
            ApplyAll(stage, t); pieces.Add(t);
        }
        return pieces;
    }

    /// <summary>情报板中心（原图，无底板）：有图用图，没图就用大字。</summary>
    private static List<StoryTeller.Item> CenterVisual(Sprite art, string fallbackWord, float x, float y, float size)
    {
        List<StoryTeller.Item> made = new List<StoryTeller.Item>();
        if (art != null)
        {
            StoryTeller.Item pic = Frameless(art, new Vector2(x, y), new Vector2(size, size));
            if (pic != null) { pic.sortingOrder = 4; ApplyAll(stage, pic); made.Add(pic); }
            return made;
        }
        return WordCenter(fallbackWord, x, y);
    }

    /// <summary>情报板中心：用户放了 png 就直接用原图（无底板），没放就用传进来的代码视觉。</summary>
    private static List<StoryTeller.Item> CenterArt(string slot, List<StoryTeller.Item> fallback, float x, float y)
    {
        Sprite art = Art(slot);
        if (art == null) return fallback;
        StoryTeller.Item pic = Frameless(art, new Vector2(x, y), new Vector2(4.4f, 3.0f));
        if (pic == null) return fallback;
        pic.sortingOrder = 4;
        ApplyAll(stage, pic);
        return new List<StoryTeller.Item> { pic };
    }

    /// <summary>把情报板的中心视觉整个换掉：旧的滑出，新的滑入。</summary>
    private static List<StoryTeller.Item> SwapCenter(StoryTeller s, List<StoryTeller.Item> oldCenter, List<StoryTeller.Item> newCenter)
    {
        if (oldCenter != null)
            foreach (StoryTeller.Item o in oldCenter)
                if (o != null) s.StartCoroutine(s.SlideOut(o, StoryTeller.Direction.Right, 3.5f, 0.16f));
        if (newCenter != null)
            foreach (StoryTeller.Item n in newCenter)
                if (n != null) s.StartCoroutine(s.SlideIn(n, StoryTeller.Direction.Left, 3.2f, 0.2f, 0f, new Vector2(0.92f, 0.92f)));
        return newCenter;
    }

    private static void SetCat(StoryTeller s, StoryTeller.Item cap, string cat)
    {
        if (cap == null) return;
        SetTextHard(cap, StageStyle.SizeTag(0.4f) + "<color=#C9D4E6>" + cat + "</color></size>");
        s.StartCoroutine(s.Pop(cap, new Vector2(0.96f, 0.96f), Vector2.one, 0.2f));
    }

    private static void SlideInPlate(StoryTeller s, List<StoryTeller.Item> plate, StoryTeller.Direction from, float dist, float dur, Vector2? scaleFrom)
    {
        if (plate == null) return;
        foreach (StoryTeller.Item it in plate)
            if (it != null) s.StartCoroutine(s.SlideIn(it, from, dist, dur, 0f, scaleFrom));
    }

    private static IEnumerator Out(StoryTeller s, List<StoryTeller.Item> on)
    {
        yield return SlideOutAll(s, on, StoryTeller.Direction.Botton, OutDist);
        yield return s.WaitStage(Gap);
    }

    /// <summary>整组卡片一起移动（可选缩放、淡出）。锚点 = 组里第一块。舞台时钟推进。</summary>
    private static IEnumerator MovePlate(StoryTeller s, List<StoryTeller.Item> plate, Vector2 to, float dur, bool fade, Vector2? scaleTo)
    {
        if (s == null || plate == null || plate.Count == 0) yield break;
        StoryTeller.Item anchor = plate[0];
        if (anchor == null || anchor.realOb == null) yield break;

        Vector2 d = to - anchor.position;
        List<Vector2> from = new List<Vector2>(plate.Count);
        List<Vector2> sc0 = new List<Vector2>(plate.Count);
        for (int i = 0; i < plate.Count; i++)
        {
            from.Add(plate[i] != null ? plate[i].position : anchor.position);
            sc0.Add(plate[i] != null ? plate[i].scale : Vector2.one);
        }
        Vector2 sc1 = scaleTo ?? sc0[0];

        float t = 0f;
        while (t < dur)
        {
            yield return s.WaitForCaptureUpdate();
            t += s.ClockDelta;
            float k = Mathf.Clamp01(t / dur);
            float e = k * k * (3f - 2f * k);
            for (int i = 0; i < plate.Count; i++)
            {
                StoryTeller.Item it = plate[i];
                if (it == null || it.realOb == null) continue;
                it.SetLocalPosition(from[i] + d * e);
                if (fade) it.SetAlpha(1f - e);
                if (scaleTo.HasValue) it.SetScale(Vector2.Lerp(sc0[i], sc1, e));
            }
        }
        for (int i = 0; i < plate.Count; i++)
        {
            StoryTeller.Item it = plate[i];
            if (it == null || it.realOb == null) continue;
            it.SetLocalPosition(from[i] + d);
            if (fade) it.SetAlpha(0f);
            if (scaleTo.HasValue) it.SetScale(sc1);
        }
    }

    /// <summary>原地转个角度（发牌 / 横穿时的旋转）。走舞台时钟，直接写 transform，不重建网格。</summary>
    private static IEnumerator TiltTo(StoryTeller s, StoryTeller.Item item, float from, float to, float dur)
    {
        if (s == null || item == null || item.realOb == null) yield break;
        float t = 0f;
        while (t < dur)
        {
            yield return s.WaitForCaptureUpdate();
            t += s.ClockDelta;
            float k = Mathf.Clamp01(t / dur);
            k = k * k * (3f - 2f * k);
            item.angle = Mathf.Lerp(from, to, k);
            if (item.realOb != null) item.realOb.transform.localEulerAngles = new Vector3(0f, 0f, item.angle);
        }
        item.angle = to;
        if (item.realOb != null) item.realOb.transform.localEulerAngles = new Vector3(0f, 0f, to);
    }

    /// <summary>飞到目标点并淡出消失（+1 那种小字用）。</summary>
    private static IEnumerator FlyAndVanish(StoryTeller s, StoryTeller.Item it, Vector2 to, float dur)
    {
        if (it == null || s == null) yield break;
        yield return MovePlate(s, new List<StoryTeller.Item> { it }, to, dur, true, null);
        it.Delete();
    }
    #endregion
}
