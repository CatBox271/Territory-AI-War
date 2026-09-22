using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 移动前的「炮塔视野截图」：以炮塔为圆心、半径 = 1.0 × 当前最大移动距离 的圆形俯视图
/// （半径倍率见 RadiusFactor，1 = 整个最大移动范围都拍进图里）。
///
/// 为什么要有它：AI 看不到场上别的东西，移动时很容易一头扎进别人的火力范围，或者正好停在别人身上。
/// 所以每次 move_turret **预览**时给它一张自己周围的俯视图，让它先看清再决定方向与距离。
///
/// 约定：
/// * 圆形之外、以及地图之外一律涂成不透明纯色 —— 不拍场外（避免模型把画外的东西当情报）；
/// * 渲染的那一瞬间把场上所有 TMP 文本临时关掉（飘字 / 气泡 / 立绘文本…），渲染完立刻还原：
///   全在同一帧内完成，屏幕上看不到闪烁；
/// * 用一台自己的正交相机手动 Render，不碰玩家的战场摄像机；
/// * PNG 同时存到 Application.persistentDataPath/MoveShots/ 方便查 bug，并返回 data:URL 给模型。
/// </summary>
public static class MoveSightCapture
{
    /// <summary>关掉就完全不截图（连 data:URL 都不生成，不占请求 token）。</summary>
    public static bool Enabled = true;
    /// <summary>半径倍率：半径 = 这个值 × 炮塔当前最大移动距离。1 = 整个最大移动范围都在这张图里。</summary>
    public static float RadiusFactor = 1f;
    /// <summary>输出图片边长（像素，正方形）。</summary>
    public static int Resolution = 512;
    /// <summary>PNG 存盘目录名（在 Application.persistentDataPath 下）。</summary>
    public static string FolderName = "MoveShots";
    /// <summary>圆形视野之外的填充色（不透明，免得透明区被模型当成内容）。</summary>
    public static Color32 OutsideColor = new Color32(8, 8, 12, 255);
    /// <summary>地图之外的填充色（略偏红，和「圆外」区分开）。</summary>
    public static Color32 MapOutsideColor = new Color32(40, 14, 14, 255);

    /// <summary>标签文字的世界高度（世界单位）。512 图、半径 4.5 时约 23 像素高。</summary>
    public static float LabelHeight = 0.40f;
    /// <summary>标签底边与实体标记之间的空隙（世界单位）。</summary>
    public static float LabelGap = 0.28f;
    /// <summary>标签颜色：自己 = 绿（和绿色标记环一致），对手 = 白（材质自带黑描边，压在什么底色上都读得清）。</summary>
    private static readonly Color LabelMineColor = new Color32(150, 240, 160, 255);
    private static readonly Color LabelOtherColor = Color.white;
    /// <summary>标签用的 TMP 预制（随便哪座炮塔的 ShowTMP：白字 + 黑描边材质）。</summary>
    private static GameObject labelTemplate;

    public class Shot
    {
        public bool ok;
        public string error;
        /// <summary>data:image/png;base64,...（塞进 user 消息的 image_url 块）。</summary>
        public string dataUrl;
        /// <summary>存盘路径（查 bug 用）。</summary>
        public string savedPath;
        /// <summary>圆形半径（世界单位）。</summary>
        public float radius;
        /// <summary>当时的最大移动距离。</summary>
        public float maxMove;
        /// <summary>边长（像素）。</summary>
        public int size;
        public byte[] png;
    }

    private static Camera cam;

    /// <summary>
    /// 开关（会先从 AIAgent 面板同步一次参数）。ReactionSystem 用它判断要不要走截图流程。
    /// </summary>
    public static bool IsEnabled { get { SyncFromAgent(); return Enabled; } }

    /// <summary>开关 / 分辨率在 AIAgent 面板上；**半径倍率在 MapConfig**（玩法口径，和移动距离配套）。</summary>
    private static void SyncFromAgent()
    {
        MapConfig cfg = MapConfig.Instance;
        if (cfg != null) RadiusFactor = cfg.moveSightRadiusFactor;

        AIAgent agent = AIAgent.Instance;
        if (agent == null) return;
        Enabled = agent.moveSightEnabled;
        Resolution = agent.moveSightResolution;
    }

    private static Camera EnsureCamera()
    {
        if (cam != null) return cam;

        var go = new GameObject("MoveSightCamera") { hideFlags = HideFlags.HideAndDontSave };
        cam = go.AddComponent<Camera>();
        cam.enabled = false;                  // 只手动 Render，不参与正常渲染顺序
        cam.orthographic = true;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 100f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
        cam.cullingMask = ~0;                 // 战场实体分布在 Default 和 UI 两个层上，这里都要
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        return cam;
    }

    /// <summary>
    /// 把这张图里**看得见的东西**写成文字坐标：方位 + 距离 + 世界坐标。
    /// 图上有位置、但一个坐标都没有 —— AI 得靠自己把"左上方那个点"心算成世界坐标，
    /// 而穿甲弹要打 0.4 半径的炮塔本体，光看图瞄不准（用户问：明明都出现了为什么不狙）。
    /// 只列圆内、地图内的实体；最多列 maxItems 条，免得把提示词撑大。
    /// </summary>
    public static string DescribeVisible(Vector2 center, float radius, int myStage, int maxItems = 12)
    {
        var sb = new StringBuilder();
        int count = 0;

        void Add(string what, Vector2 pos, string extra)
        {
            if (count >= maxItems) return;
            Vector2 off = pos - center;
            if (off.magnitude > radius) return;                       // 图外
            float half = MapConfig.Instance != null ? MapConfig.Instance.worldSize * 0.5f : 5f;
            if (Mathf.Abs(pos.x) > half || Mathf.Abs(pos.y) > half) return;   // 地图外

            if (sb.Length == 0) sb.Append("【图里看见的】");
            else sb.Append('；');
            sb.Append(what);
            if (!string.IsNullOrEmpty(extra)) sb.Append(' ').Append(extra);
            sb.Append(" 在你的 ").Append(InformGetter.CardinalDirection(off));
            sb.Append(' ').Append(off.magnitude.ToString("0.0")).Append(" 格处（坐标约 (");
            sb.Append(pos.x.ToString("0.0")).Append(", ").Append(pos.y.ToString("0.0")).Append(")）");
            count++;
        }

        // 炮塔（别人的：只给位置，不给子弹量）
        for (int i = 1; i < 5; i++)
        {
            if (i == myStage) continue;
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;
            Add(AIAgent.GetStageName(i) + "的炮塔", t.transform.position, "");
        }

        // 大球 / 穿甲弹（数值和朝向都给 —— 本来图上就写着）
        foreach (BallPainter bp in UnityEngine.Object.FindObjectsOfType<BallPainter>())
        {
            if (bp == null || bp.value <= 0) continue;
            string name = bp.game_item_name;
            string extra = bp.value.ToShortString();
            Vector2 v = bp.rb != null ? bp.rb.velocity : Vector2.zero;
            if (v.sqrMagnitude > 0.0001f)
                extra += "，朝 " + InformGetter.CardinalDirection(v);
            Add((bp.stage == myStage ? "你的" : AIAgent.GetStageName(bp.stage) + "的") + name, bp.transform.position, extra);
        }

        if (sb.Length == 0) return "";
        sb.Append("。**要打谁就直接照这个坐标瞄（use_prop 的 aim_x / aim_y），不要拿图上没量的像素去猜。**");
        return sb.ToString();
    }

    /// <summary>以炮塔为圆心截一张圆形俯视图。失败时 shot.ok = false、error 里写原因（调用方照常往下走）。</summary>
    public static Shot Capture(Towel towel, int stage)
    {
        SyncFromAgent();

        var shot = new Shot();
        if (!Enabled) { shot.error = "已关闭"; return shot; }
        if (towel == null) { shot.error = "炮塔为空"; return shot; }

        float maxMove = towel.MaxMoveDistance;
        float radius = maxMove * RadiusFactor;
        if (radius <= 0.01f) { shot.error = $"半径过小（最大移动距离 {maxMove:0.###}）"; return shot; }

        shot.maxMove = maxMove;
        shot.radius = radius;
        shot.size = Mathf.Clamp(Resolution, 64, 2048);

        Vector2 center = towel.transform.position;
        Camera c = EnsureCamera();
        if (c == null) { shot.error = "截图相机创建失败"; return shot; }

        RenderTexture rt = RenderTexture.GetTemporary(shot.size, shot.size, 24, RenderTextureFormat.ARGB32);
        c.targetTexture = rt;
        c.orthographicSize = radius;
        c.transform.position = new Vector3(center.x, center.y, -10f);
        c.transform.rotation = Quaternion.identity;

        // 临时关掉所有 TMP 文本（不拍文字），渲染完马上还原
        TMP_Text[] texts = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true);
        var wasEnabled = new bool[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            wasEnabled[i] = texts[i] != null && texts[i].enabled;
            if (wasEnabled[i]) texts[i].enabled = false;
        }

        Texture2D tex = null;
        RenderTexture prev = RenderTexture.active;
        List<GameObject> labelObjs = null;
        try
        {
            // 在「场上的文字都关掉」之后、渲染之前把标签建出来：
            // 每个实体旁边写清**类型 + 归属 + 世界坐标**（自己的炮塔写【你】），渲染完立刻销毁。
            labelObjs = SpawnLabels(BuildLabels(center, radius, stage), shot.size, radius, center);

            c.Render();

            RenderTexture.active = rt;
            tex = new Texture2D(shot.size, shot.size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, shot.size, shot.size), 0, 0);
            tex.Apply();
        }
        catch (Exception e)
        {
            shot.error = "渲染/读取失败：" + e.Message;
        }
        finally
        {
            RenderTexture.active = prev;
            c.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            DestroyLabels(labelObjs);
            for (int i = 0; i < texts.Length; i++)
                if (wasEnabled[i] && texts[i] != null) texts[i].enabled = true;
        }

        if (tex == null) return shot;
        if (!string.IsNullOrEmpty(shot.error)) { UnityEngine.Object.Destroy(tex); return shot; }

        // 圆形之外 + 地图之外涂掉：不拍场外
        float half = MapConfig.Instance != null ? MapConfig.Instance.worldSize * 0.5f : 5f;
        float perPixel = (radius * 2f) / shot.size;
        Color32[] px = tex.GetPixels32();
        float r2 = radius * radius;
        for (int y = 0; y < shot.size; y++)
        {
            float wy = center.y + (y + 0.5f) * perPixel - radius;
            bool yOutMap = Mathf.Abs(wy) > half;
            int row = y * shot.size;
            for (int x = 0; x < shot.size; x++)
            {
                float wx = center.x + (x + 0.5f) * perPixel - radius;
                float dx = wx - center.x, dy = wy - center.y;
                if (dx * dx + dy * dy > r2) px[row + x] = OutsideColor;
                else if (yOutMap || Mathf.Abs(wx) > half) px[row + x] = MapOutsideColor;
            }
        }
        tex.SetPixels32(px);
        tex.Apply();

        // 刷完视野遮罩之后再画标记与数字：球的位置、球身上的数值都要出现在图里
        OverlayEntities(px, shot.size, center, radius, stage, labelObjs == null || labelObjs.Count == 0);

        tex.SetPixels32(px);
        tex.Apply();

        shot.png = tex.EncodeToPNG();
        UnityEngine.Object.Destroy(tex);

        if (shot.png == null || shot.png.Length == 0) { shot.error = "PNG 编码失败"; return shot; }

        shot.dataUrl = "data:image/png;base64," + Convert.ToBase64String(shot.png);
        shot.savedPath = Save(shot.png, stage, center, radius, maxMove);
        shot.ok = true;
        return shot;
    }

    /// <summary>
    /// 在图上标出「球在哪、球身上写的数值是多少」。
    ///
    /// 为什么必须画进图片：这张图是给模型看的，模型看不到 Unity 的 UI 文字（渲染前那些 TMP 也会被临时关掉），
    /// 所以数值只能我们自己在像素上写一遍。用的是 PixelDraw 那套 5×7 点阵字，字符集就是
    /// HugeInt.ToShortString() 的取值集合（0-9 . - K M B T P）。
    ///
    /// 画的东西：
    /// * 大球 / 穿甲弹：圆环（穿甲是叉）标记 + 一条**带箭头的粗线**指向速度方向 + 上方数值
    /// * 炮塔本体：圆点，**一律不写数值**；自己那台套绿环、别人的套红环
    ///
    /// 颜色按**请求方**（myStage）判：绿 = 你的、红 = 对手的、白 = 中立。
    /// 旧写法是 `bp.stage == 1 ? enemy : mine` —— 把"1 号"写死成敌人，
    /// 于是**红色（1 号）自己看这张图时自己/敌人整个反了**。
    ///
    /// drawBallValues：球身上那串数值要不要用点阵字再写一遍。
    /// 标签（SpawnLabels 的世界空间 TMP 文字）里已经带着**归属 + 类型 + 数值 + 坐标**，
    /// 这时候就不重复写了；标签建不出来（没有 TMP 预制）才退回点阵数值。
    /// </summary>
    private static void OverlayEntities(Color32[] px, int size, Vector2 center, float radius, int myStage,
        bool drawBallValues = true)
    {
        if (px == null) return;

        Color32 labelBg = new Color32(12, 12, 16, 220);
        Color32 labelColor = new Color32(255, 255, 255, 255);
        Color32 enemyEdge = new Color32(255, 120, 120, 255);
        Color32 mineEdge = new Color32(150, 240, 160, 255);

        int scale = size >= 768 ? 3 : (size >= 384 ? 2 : 1);   // 512 图 -> 每格 2px，字高 14px
        float perPixel = (radius * 2f) / size;

        // 世界坐标 -> 网格像素（y 向上为正，和 GetPixels32 下标一致）
        bool TryToPixel(Vector2 world, out int gx, out int gy)
        {
            gx = Mathf.RoundToInt((world.x - (center.x - radius)) / perPixel);
            gy = Mathf.RoundToInt((world.y - (center.y - radius)) / perPixel);
            return gx >= 0 && gy >= 0 && gx < size && gy < size;
        }

        void DrawValue(Vector2 world, string text, Color32 fill, int markerRadius, int lift)
        {
            text = PixelDraw.Filter(text);
            if (text.Length == 0) return;
            if (!TryToPixel(world, out int gx, out int gy)) return;

            // 数值写在标记**上方**（留出 lift 像素的空档），并且夹在网格里，别被裁掉
            int tw = PixelDraw.TextWidth(text, scale);
            int tx = Mathf.Clamp(gx - tw / 2, 1, Mathf.Max(1, size - tw - 1));
            int ty = Mathf.Clamp(gy + markerRadius + lift, 1, size - PixelDraw.TextHeight(scale) - 2 * scale - 1);
            PixelDraw.DrawText(px, size, tx, ty, text, scale, fill, labelBg);
        }

        int maxEntities = 40;   // 视野里几十个弹体已经很多了，别把图糊满
        int drawn = 0;

        BallPainter[] balls = UnityEngine.Object.FindObjectsOfType<BallPainter>();
        foreach (BallPainter bp in balls)
        {
            if (drawn >= maxEntities) break;
            if (bp == null || bp.value <= 0) continue;      // 已经归零的就不标了

            Vector2 pos = bp.transform.position;
            if (!TryToPixel(pos, out int gx, out int gy)) continue;
            if (Mathf.Abs(pos.x) > (MapConfig.Instance != null ? MapConfig.Instance.worldSize * 0.5f : 5f)) continue; // 图外的不标

            bool shell = bp.game_item_name == "穿甲";
            int markerR = shell ? 3 : Mathf.Clamp(Mathf.RoundToInt(bp.transform.lossyScale.x / perPixel * 0.5f), 3, 26);
            Color32 edge = bp.stage == 0 ? labelColor : (bp.stage == myStage ? mineEdge : enemyEdge);

            // 标记本体：大球按实际半径画圆环（一眼看出大小），穿甲弹画小方块
            if (shell)
            {
                PixelDraw.DrawLine(px, size, gx - markerR, gy - markerR, gx + markerR, gy + markerR, 2, edge);
                PixelDraw.DrawLine(px, size, gx - markerR, gy + markerR, gx + markerR, gy - markerR, 2, edge);
            }
            else
            {
                PixelDraw.DrawRing(px, size, gx, gy, markerR, Mathf.Max(1, scale), edge);
            }

            // 朝向短棒 + 箭头：从球心指向速度方向，长度按"这一帧能跑多远"放大到看得见。
            // 大球的走向是躲/拦的关键，原来只有一根细棍、还短，看不清 → 加粗 + 画箭头。
            Vector2 v = bp.rb != null ? bp.rb.velocity : Vector2.zero;
            if (v.sqrMagnitude > 0.0001f)
            {
                Vector2 dir = v.normalized;
                int thick = Mathf.Max(2, scale);
                int len = Mathf.Clamp(Mathf.RoundToInt(v.magnitude / perPixel * 0.4f), markerR + 6, size / 3);
                int tipX = gx + Mathf.RoundToInt(dir.x * len);
                int tipY = gy + Mathf.RoundToInt(dir.y * len);
                PixelDraw.DrawLine(px, size, gx, gy, tipX, tipY, thick, edge);

                float back = Mathf.Atan2(-dir.y, -dir.x);
                int headLen = Mathf.Max(6, markerR + 2);
                for (int k = 0; k < 2; k++)
                {
                    float a = back + (k == 0 ? 0.45f : -0.45f);
                    int hx = tipX + Mathf.RoundToInt(Mathf.Cos(a) * headLen);
                    int hy = tipY + Mathf.RoundToInt(Mathf.Sin(a) * headLen);
                    PixelDraw.DrawLine(px, size, tipX, tipY, hx, hy, thick, edge);
                }
            }

            if (drawBallValues) DrawValue(pos, bp.value.ToShortString(), labelColor, markerR, 3);
            drawn++;
        }

        // 炮塔只画位置，**一律不标数值**（自己的子弹量情报正文里有；别人的子弹量是私有情报）。
        // 自己那台套绿环、别人的套红环，名字与坐标由标签（世界空间 TMP 文字）写在旁边。
        MapConfig cfg = MapConfig.Instance;
        for (int i = 1; i < 5; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;

            Vector2 pos = t.transform.position;
            if (!TryToPixel(pos, out int gx, out int gy)) continue;

            bool mine = i == myStage;
            Color32 color = mine
                ? mineEdge
                : (cfg != null ? (Color32)cfg.GetColor(i, MapConfig.ColorStage.Towel) : labelColor);
            PixelDraw.DrawDisc(px, size, gx, gy, Mathf.Max(2, scale * 2), color);
            PixelDraw.DrawRing(px, size, gx, gy, Mathf.Max(5, scale * 3 + 2), Mathf.Max(1, scale),
                mine ? mineEdge : enemyEdge);
        }
    }

    #region 标签（世界空间 TMP 文字）

    /// <summary>图上要写的一条标签。</summary>
    private class SightLabel
    {
        public string text;
        /// <summary>实体位置（世界坐标）。</summary>
        public Vector2 at;
        /// <summary>实体标记在世界单位里的半径（标签压在它上方）。</summary>
        public float radius;
        public Color color;
    }

    /// <summary>
    /// 收集这张图里要标的实体（和 OverlayEntities 画的是同一批）：
    /// 炮塔先标、球后标 —— 挤在一起时先来的占原位、后来的往上让。
    ///
    /// 写进标签的三样就是用户要的：**类型**（炮塔 / 大球 / 穿甲 …）、**归属**（【你】或名字）、
    /// **世界坐标**。点阵字只有数字，写不出中文，所以标签走世界空间 TMP 文字。
    /// </summary>
    private static List<SightLabel> BuildLabels(Vector2 center, float radius, int myStage)
    {
        var list = new List<SightLabel>();
        float half = MapConfig.Instance != null ? MapConfig.Instance.worldSize * 0.5f : 5f;

        bool InView(Vector2 p) =>
            (p - center).sqrMagnitude <= radius * radius &&
            Mathf.Abs(p.x) <= half && Mathf.Abs(p.y) <= half;

        string Coord(Vector2 p) => "(" + p.x.ToString("0.0") + "," + p.y.ToString("0.0") + ")";

        for (int i = 1; i < 5; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;
            Vector2 p = t.transform.position;
            if (!InView(p)) continue;

            bool mine = i == myStage;
            list.Add(new SightLabel
            {
                text = (mine ? "【你】炮塔" : AIAgent.GetStageName(i) + "炮塔") + Coord(p),
                at = p,
                radius = 0.45f,
                color = mine ? LabelMineColor : LabelOtherColor,
            });
        }

        foreach (BallPainter bp in UnityEngine.Object.FindObjectsOfType<BallPainter>())
        {
            if (bp == null || bp.value <= 0) continue;
            Vector2 p = bp.transform.position;
            if (!InView(p)) continue;

            bool mine = bp.stage == myStage;
            string owner = mine ? "你的" : (bp.stage == 0 ? "中立的" : AIAgent.GetStageName(bp.stage) + "的");
            list.Add(new SightLabel
            {
                text = owner + bp.game_item_name + bp.value.ToShortString() + Coord(p),
                at = p,
                radius = Mathf.Max(0.25f, bp.transform.lossyScale.x * 0.5f),
                color = mine ? LabelMineColor : LabelOtherColor,
            });
        }

        return list;
    }

    /// <summary>标签模板：随便找一座炮塔的 ShowTMP 预制（白字 + 黑描边的 TMP 材质）。</summary>
    private static GameObject FindLabelTemplate()
    {
        if (labelTemplate != null) return labelTemplate;
        foreach (var kv in Towel.AllTowel)
        {
            Towel t = kv.Value;
            if (t != null && t.tipPrefab != null) { labelTemplate = t.tipPrefab; break; }
        }
        return labelTemplate;
    }

    /// <summary>建一条标签文字（世界空间 TMP，不透明白字黑描边）。失败返回 null。</summary>
    private static GameObject CreateLabel(string text, Color color)
    {
        GameObject go = null;
        try
        {
            GameObject template = FindLabelTemplate();
            TextMeshPro tmp;
            if (template != null)
            {
                go = UnityEngine.Object.Instantiate(template);
                go.name = "MoveSightLabel";
                TowelTip tip = go.GetComponent<TowelTip>();
                if (tip != null) UnityEngine.Object.Destroy(tip);      // 只要一行字，不要飘字动画
                tmp = go.GetComponent<TextMeshPro>();
            }
            else
            {
                go = new GameObject("MoveSightLabel", typeof(RectTransform));
                tmp = go.AddComponent<TextMeshPro>();
                TMP_FontAsset font = TMP_Settings.defaultFontAsset;
                if (font != null) { tmp.font = font; tmp.fontSharedMaterial = font.material; }
            }

            if (tmp == null) { UnityEngine.Object.Destroy(go); return null; }

            tmp.enableAutoSizing = false;
            tmp.fontSize = 32f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.color = color;
            tmp.text = text;
            if (tmp.rectTransform != null) tmp.rectTransform.sizeDelta = new Vector2(800f, 120f);

            // 字高归一到 LabelHeight：预制与字体的字号口径不一样，量一次再乘缩放最省事
            tmp.ForceMeshUpdate();
            float worldH = tmp.textBounds.size.y * Mathf.Abs(go.transform.lossyScale.y);
            if (worldH > 0.0001f) go.transform.localScale *= LabelHeight / worldH;

            return go;
        }
        catch (Exception e)
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            Debug.LogWarning("[移动截图] 标签创建失败：" + e.Message);
            return null;
        }
    }

    /// <summary>
    /// 把标签摆到图上：水平对自己那个实体，竖直压在标记上方；
    /// 和已有标签重叠时往上让（最多让 12 次），别把图糊成一团。
    /// 返回所有建出来的对象，渲染完由 DestroyLabels 销毁。
    /// </summary>
    private static List<GameObject> SpawnLabels(List<SightLabel> items, int size, float radius, Vector2 center)
    {
        var spawned = new List<GameObject>();
        if (items == null || items.Count == 0) return spawned;

        float perPixel = (radius * 2f) / size;
        var used = new List<Rect>();

        foreach (SightLabel lb in items)
        {
            GameObject go = CreateLabel(lb.text, lb.color);
            if (go == null) continue;

            TextMeshPro tmp = go.GetComponent<TextMeshPro>();
            float worldW = tmp != null ? tmp.textBounds.size.x * Mathf.Abs(go.transform.lossyScale.x) : 1f;
            float halfW = Mathf.Clamp(worldW * 0.5f / perPixel, 4f, size * 0.45f);
            float halfH = Mathf.Max(3f, LabelHeight * 0.5f / perPixel);

            // 世界坐标 -> 像素（和 OverlayEntities 同一套换算）
            float px = Mathf.Clamp((lb.at.x - (center.x - radius)) / perPixel, halfW + 1f, size - halfW - 1f);
            float py = (lb.at.y - (center.y - radius)) / perPixel;

            float bottom = py + Mathf.Max(2f, lb.radius / perPixel) + LabelGap / perPixel;
            Rect rect = new Rect(px - halfW, bottom, halfW * 2f, halfH * 2f);
            for (int tries = 0; tries < 12; tries++)
            {
                rect = new Rect(px - halfW, bottom, halfW * 2f, halfH * 2f);
                bool hit = false;
                foreach (Rect r in used)
                    if (r.Overlaps(rect)) { hit = true; break; }
                if (!hit) break;
                bottom += halfH * 2f + 2f;
            }
            used.Add(rect);

            float wx = center.x - radius + px * perPixel;
            float wy = center.y - radius + (rect.y + halfH) * perPixel;
            go.transform.position = new Vector3(wx, wy, -2f);       // z 拉到最前，压在地图之上

            spawned.Add(go);
        }

        return spawned;
    }

    /// <summary>渲染完立刻销毁这次建的标签（不留任何残留物在场景里）。</summary>
    private static void DestroyLabels(List<GameObject> labels)
    {
        if (labels == null) return;
        foreach (GameObject go in labels)
            if (go != null) UnityEngine.Object.Destroy(go);
        labels.Clear();
    }

    #endregion

    /// <summary>存盘（查 bug 用）。文件名带阵营/坐标/半径/时间，方便对照。</summary>
    private static string Save(byte[] png, int stage, Vector2 pos, float radius, float maxMove)
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, FolderName);
            Directory.CreateDirectory(dir);
            string name = $"move_s{stage}_{DateTime.Now:yyyyMMdd_HHmmss_fff}" +
                          $"_pos{pos.x:0.0}_{pos.y:0.0}_r{radius:0.00}_max{maxMove:0.00}.png";
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[移动截图] 存盘失败：{e.Message}");
            return null;
        }
    }
}
