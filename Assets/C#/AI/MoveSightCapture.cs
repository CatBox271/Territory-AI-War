using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 移动前的「炮塔视野截图」：**以地图中心 (0,0) 为中心、完整覆盖整张地图的正方形俯视图**
/// （2026-09-22 用户口径："应该用以(0,0)点为拍摄中心，拍(-5,-5)到(5,5)的正方形"），
/// 后处理只保留「炮塔可视半径（以炮塔为圆心、半径 = 半径倍率 × 当前最大移动距离）∪ 自己的领土（外扩几像素）」，
/// 其余抹成纯黑（用户："其余用黑色剔除"）—— 图上亮着的就是它这一轮看得见的东西。
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
    /// <summary>圆形视野之外的填充色 —— **已不再使用**（现在统一"压暗"处理，圆外/地图外都保留原色只是变暗）。
    /// 留着字段是为了万一想切回"涂一片色"的老做法。</summary>
    public static Color32 OutsideColor = new Color32(8, 8, 12, 255);
    /// <summary>地图之外的填充色 —— **已不再使用**：炮塔站在地图角上时这块红盘会占掉大半张图（用户："你看你做的一坨"）。</summary>
    public static Color32 MapOutsideColor = new Color32(40, 14, 14, 255);

    /// <summary>标签文字的世界高度（世界单位）。**整张地图**铺满 1024 图时，0.19 ≈ 19 像素高
    /// （2026-09-22 用户两次说小一点："你标注的字可以小一点" → 0.40→0.26，"字还能小点" → 0.26→0.19）。</summary>
    public static float LabelHeight = 0.19f;
    /// <summary>标签底边与实体标记之间的空隙（世界单位）。</summary>
    public static float LabelGap = 0.28f;
    /// <summary>标签颜色：自己 = 绿（和绿色标记环一致），对手 = 白（材质自带黑描边，压在什么底色上都读得清）。</summary>
    /// <summary>标签一律黑色（2026-09-22 用户："统一用黑色加标注…黑色字，不要背景"）；阵营写在文字里。</summary>
    private static readonly Color LabelInkColor = Color.black;
    /// <summary>标签的**白描边**宽度（TMP 的 _OutlineWidth 口径，和 DisplayValue 同一套）。字变小了，描边也跟着细一点。</summary>
    public static float LabelOutlineWidth = 0.28f;
    /// <summary>这次截图建出来的标签材质实例（TMP 描边要改材质，必须自己收回，不然每次截图漏一个）。</summary>
    private static readonly List<Material> labelMaterials = new();

    /// <summary>
    /// 标签专用的 Layer（**第二遍渲染只拍这一层**）。
    /// 2026-09-22 的 bug：第二遍没换 cullingMask，于是"只拍标签"其实把整个场景又拍了一遍，
    /// 而不透明的场景内容在合成时把抹黑后的图**整张覆盖**掉 —— 表现就是"后处理没生效、整张地图全亮"。
    /// 9 号层在 ProjectSettings/TagManager.asset 里是空的（只用到 0,1,2,4,5,6,7,8）—— 用之前确认一下它还是空的。
    /// </summary>
    private const int LabelLayer = 9;
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
    /// <summary>
    /// 自己的领土在图上**向外延展**的世界距离（2026-09-22 用户："领土向外延展几个像素。让接壤的是谁更清楚。"）。
    /// 延展之后紧贴自己领土的那一圈**邻居的地**也留在图里 —— 看颜色就知道接壤的是谁，不然边界外面全是黑的。
    /// 0.06 世界单位：1024 图 ≈ 6 像素、512 图 ≈ 3 像素；0 = 不延展。
    /// </summary>
    public static float TerritoryBleedWorld = 0.06f;

    /// <summary>这个点是不是**自己**的领土（查 TerritoryCanvas.territoryMap，CPU 端逐像素 byte，0 = 无主）。
    /// 抹黑、画标记、写标签清单三处都用它，判据必须一致。</summary>
    private static bool IsMyLandAt(Vector2 world, int myStage)
    {
        MapConfig cfg = MapConfig.Instance;
        var tcv = TerritoryCanvas.Instance;
        if (cfg == null || tcv == null || !tcv.territoryMap.IsCreated || cfg.resolution <= 0) return false;
        float half = cfg.worldSize * 0.5f;
        if (Mathf.Abs(world.x) > half || Mathf.Abs(world.y) > half) return false;   // 地图外没有归属
        int res = cfg.resolution;
        int mx = Mathf.Clamp((int)((world.x / cfg.worldSize + 0.5f) * res), 0, res - 1);
        int my = Mathf.Clamp((int)((world.y / cfg.worldSize + 0.5f) * res), 0, res - 1);
        return tcv.territoryMap[my * res + mx] == (byte)myStage;
    }

    /// <summary>自己的领土 + 向外 bleed 世界单位（四向各采一次，够近似"延展几个像素"）。</summary>
    private static bool IsMyLandNear(Vector2 world, int myStage, float bleed)
    {
        if (IsMyLandAt(world, myStage)) return true;
        if (bleed <= 0f) return false;
        return IsMyLandAt(new Vector2(world.x + bleed, world.y), myStage)
            || IsMyLandAt(new Vector2(world.x - bleed, world.y), myStage)
            || IsMyLandAt(new Vector2(world.x, world.y + bleed), myStage)
            || IsMyLandAt(new Vector2(world.x, world.y - bleed), myStage);
    }

    /// <summary>
    /// **实体**能不能标：中心点可见，或者它自己身上（半径四向）有一点可见。
    /// 为什么按中心点判不够：领土边界是逐像素的，物体中心正好压在一格"不是我的"上（未涂的小缝、
    /// 对方子弹刷过的一格）时，按中心判就会漏标 —— 用户 2026-09-22 看到的就是
    /// "领土里那颗穿甲没有标注"。抹黑那一侧不变（那是逐像素的），这里只放宽"标不标"。
    /// </summary>
    private static bool EntityVisible(Vector2 sightCenter, float sightRadius, int myStage, Vector2 pos, float radius)
    {
        if (VisibleAt(sightCenter, sightRadius, myStage, pos)) return true;
        float r = Mathf.Max(radius, 0.05f);
        return VisibleAt(sightCenter, sightRadius, myStage, new Vector2(pos.x + r, pos.y))
            || VisibleAt(sightCenter, sightRadius, myStage, new Vector2(pos.x - r, pos.y))
            || VisibleAt(sightCenter, sightRadius, myStage, new Vector2(pos.x, pos.y + r))
            || VisibleAt(sightCenter, sightRadius, myStage, new Vector2(pos.x, pos.y - r));
    }

    /// <summary>
    /// 这个点**看不看得见**：在炮塔的"可视半径"（圆）内，或者落在**自己的领土**上
    /// （2026-09-22 用户："保留炮塔可视半径∪他自己的领土"；领土按 TerritoryBleedWorld 向外延展一点，
    /// 这样接壤的邻居也露得出来）。图和【图里看见的】清单用同一个判据。
    /// </summary>
    public static bool VisibleAt(Vector2 sightCenter, float sightRadius, int myStage, Vector2 pos)
        => (pos - sightCenter).sqrMagnitude <= sightRadius * sightRadius
           || IsMyLandNear(pos, myStage, TerritoryBleedWorld);

    /// <summary>
    /// 这个点**看不看得见**：视野 = 以 center 为圆心、radius 的圆 ∪ **自己的领土**。
    /// 领土归属查 TerritoryCanvas.territoryMap（CPU 端逐像素 byte，0 = 无主/中立），不用回读 GPU。
    /// </summary>
    public static bool CanSee(Vector2 center, float radius, int myStage, Vector2 pos)
    {
        MapConfig cfg = MapConfig.Instance;
        float half = cfg != null ? cfg.worldSize * 0.5f : 5f;
        if (Mathf.Abs(pos.x) > half || Mathf.Abs(pos.y) > half) return false;      // 地图外
        return VisibleAt(center, radius, myStage, pos);
    }

    public static string DescribeVisible(Vector2 center, float radius, int myStage, int maxItems = 12)
    {
        var sb = new StringBuilder();
        int count = 0;

        void Add(string what, Vector2 pos, string extra)
        {
            if (count >= maxItems) return;
            // 实体级判据（中心点或身上有一点可见），和图上标注同一套 —— 不然会出现"图上有、清单里没有"
            if (!EntityVisible(center, radius, myStage, pos, 0.25f)) return;
            Vector2 off = pos - center;
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

    /// <summary>截一张「整张地图」的俯视图（相机固定在 (0,0)），再按可见范围抹黑。失败时 shot.ok = false、error 里写原因（调用方照常往下走）。
    /// tag：文件名前缀（空 = AI 自己预览拍的；"auto" = 【临时调试】定时拍的那批，一眼分得开）。</summary>
    public static Shot Capture(Towel towel, int stage, string tag = "")
    {
        SyncFromAgent();

        var shot = new Shot();
        if (!Enabled) { shot.error = "已关闭"; return shot; }
        if (towel == null) { shot.error = "炮塔为空"; return shot; }

        float maxMove = towel.MaxMoveDistance;
        float radius = maxMove * RadiusFactor;                 // 炮塔自己的"可视半径"（圆）
        if (radius <= 0.01f) { shot.error = $"半径过小（最大移动距离 {maxMove:0.###}）"; return shot; }

        MapConfig cfg = MapConfig.Instance;
        float half = cfg != null ? cfg.worldSize * 0.5f : 5f;   // 地图 = [-half, half]²

        shot.maxMove = maxMove;
        shot.radius = radius;
        shot.size = Mathf.Clamp(Resolution, 64, 2048);

        // 2026-09-22 用户口径："应该用以(0,0)点为拍摄中心，拍(-5,-5)到(5,5)的正方形" ——
        // 相机**固定**对准地图中心、正好装下整张地图；"看得见哪儿"靠后处理抹黑来表达，
        // 不再让相机跟着炮塔跑（那样炮塔在角上时整张图大半是场外）。
        Vector2 viewCenter = Vector2.zero;
        Vector2 sightCenter = towel.transform.position;

        Camera c = EnsureCamera();
        if (c == null) { shot.error = "截图相机创建失败"; return shot; }

        RenderTexture rt = RenderTexture.GetTemporary(shot.size, shot.size, 24, RenderTextureFormat.ARGB32);
        c.targetTexture = rt;
        c.orthographicSize = half;                             // 竖直半高 = worldSize/2 → 画面正好是整张地图
        c.transform.position = new Vector3(viewCenter.x, viewCenter.y, -10f);
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
        RenderTexture labelRT = null;
        Color32[] labelPx = null;
        List<GameObject> labelObjs = null;
        CameraClearFlags prevClear = c.clearFlags;
        Color prevBg = c.backgroundColor;
        int prevMask = c.cullingMask;
        try
        {
            // 标签先建出来但**先不启用**：整张地图里大部分区域会被抹黑，
            // 标签必须等抹黑之后再单独合成上去，否则落在黑区里的字会跟着一起被抹掉
            // （2026-09-22 用户："对图片后处理…其余用黑色剔除"）。
            labelObjs = SpawnLabels(BuildLabels(viewCenter, half, sightCenter, radius, stage), shot.size, half, viewCenter);
            foreach (GameObject go in labelObjs) if (go != null) go.SetActive(false);

            c.Render();                                        // 第一遍：只有场景（场上的文字已全关）

            RenderTexture.active = rt;
            tex = new Texture2D(shot.size, shot.size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, shot.size, shot.size), 0, 0);
            tex.Apply();

            // 第二遍：只拍标签，背景清成**全透明**；一会儿按 alpha 贴到抹黑后的图上。
            // **必须把 cullingMask 换成只有标签那一层** —— 否则这一遍会把整个场景再拍一遍，
            // 不透明的场景像素在合成时会把抹黑后的图整张覆盖掉（2026-09-22 的 bug：
            // 表现是"后处理完全没生效、整张地图全亮"）。
            labelRT = RenderTexture.GetTemporary(shot.size, shot.size, 24, RenderTextureFormat.ARGB32);
            foreach (GameObject go in labelObjs) if (go != null) go.SetActive(true);
            c.cullingMask = 1 << LabelLayer;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0f, 0f, 0f, 0f);
            c.targetTexture = labelRT;
            c.Render();

            RenderTexture.active = labelRT;
            var labelTex = new Texture2D(shot.size, shot.size, TextureFormat.RGBA32, false);
            labelTex.ReadPixels(new Rect(0, 0, shot.size, shot.size), 0, 0);
            labelTex.Apply();
            labelPx = labelTex.GetPixels32();
            UnityEngine.Object.Destroy(labelTex);
        }
        catch (Exception e)
        {
            shot.error = "渲染/读取失败：" + e.Message;
        }
        finally
        {
            RenderTexture.active = prev;
            c.cullingMask = prevMask;
            c.clearFlags = prevClear;
            c.backgroundColor = prevBg;
            c.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            if (labelRT != null) RenderTexture.ReleaseTemporary(labelRT);
            DestroyLabels(labelObjs);
            for (int i = 0; i < texts.Length; i++)
                if (wasEnabled[i] && texts[i] != null) texts[i].enabled = true;
        }

        if (tex == null) return shot;
        if (!string.IsNullOrEmpty(shot.error)) { UnityEngine.Object.Destroy(tex); return shot; }

        // —— 后处理：可见 = **可视半径 ∪ (自己的领土向外延展 TerritoryBleedWorld)**，其余一律抹黑 ——
        // 2026-09-22 用户："保留炮塔可视半径∪他自己的领土。其余用黑色剔除" +
        //              "领土向外延展几个像素。让接壤的是谁更清楚。"
        // 延展只做在自己领土那一侧（用两趟一维膨胀，O(像素)），邻居的地因此会在边界外露出一圈，
        // 看颜色就知道接壤的是谁；可视圆不延展（那圈之外本来就看不见）。
        float perPixel = (half * 2f) / shot.size;              // 整张地图铺满这张图
        Color32[] px = tex.GetPixels32();
        float r2 = radius * radius;
        Color32 black = new Color32(0, 0, 0, 255);

        int n = shot.size * shot.size;
        bool[] mine = new bool[n];                             // 自己的领土
        bool[] circle = new bool[n];                           // 可视半径内
        for (int y = 0; y < shot.size; y++)
        {
            float wy = viewCenter.y + (y + 0.5f) * perPixel - half;
            float dy = wy - sightCenter.y;
            int row = y * shot.size;
            for (int x = 0; x < shot.size; x++)
            {
                float wx = viewCenter.x + (x + 0.5f) * perPixel - half;
                float dx = wx - sightCenter.x;
                int i = row + x;
                circle[i] = dx * dx + dy * dy <= r2;
                mine[i] = !circle[i] && IsMyLandAt(new Vector2(wx, wy), stage);
            }
        }

        bool[] grown = (bool[])mine.Clone();
        int bleedPx = Mathf.Clamp(Mathf.RoundToInt(TerritoryBleedWorld / Mathf.Max(perPixel, 1e-6f)), 0, 64);
        if (bleedPx > 0)
        {
            for (int y = 0; y < shot.size; y++)                // 水平
            {
                int row = y * shot.size, run = 0;
                for (int x = 0; x < shot.size; x++)
                {
                    int i = row + x;
                    if (mine[i]) run = bleedPx;
                    else if (run > 0) { grown[i] = true; run--; }
                }
                run = 0;
                for (int x = shot.size - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (mine[i]) run = bleedPx;
                    else if (run > 0) { grown[i] = true; run--; }
                }
            }
            for (int x = 0; x < shot.size; x++)                // 垂直
            {
                int run = 0;
                for (int y = 0; y < shot.size; y++)
                {
                    int i = y * shot.size + x;
                    if (mine[i]) run = bleedPx;
                    else if (run > 0) { grown[i] = true; run--; }
                }
                run = 0;
                for (int y = shot.size - 1; y >= 0; y--)
                {
                    int i = y * shot.size + x;
                    if (mine[i]) run = bleedPx;
                    else if (run > 0) { grown[i] = true; run--; }
                }
            }
        }

        for (int i = 0; i < n; i++)
            if (!circle[i] && !grown[i]) px[i] = black;

        // 抹黑之后再画标记 / 箭头 / 兜底数值：只有**中心点在可见范围内**的才画
        OverlayEntities(px, shot.size, viewCenter, half, sightCenter, radius, stage,
            labelObjs == null || labelObjs.Count == 0);

        // 最后把第二遍拍的标签按 alpha 贴上去：标签永远不被抹黑
        if (labelPx != null)
            for (int i = 0; i < px.Length; i++)
                if (labelPx[i].a > 8) px[i] = labelPx[i];

        tex.SetPixels32(px);
        tex.Apply();

        shot.png = tex.EncodeToPNG();
        UnityEngine.Object.Destroy(tex);

        if (shot.png == null || shot.png.Length == 0) { shot.error = "PNG 编码失败"; return shot; }

        shot.dataUrl = "data:image/png;base64," + Convert.ToBase64String(shot.png);
        shot.savedPath = Save(shot.png, stage, sightCenter, radius, maxMove, tag);
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
    private static void OverlayEntities(Color32[] px, int size, Vector2 viewCenter, float viewHalf,
        Vector2 sightCenter, float sightRadius, int myStage, bool drawBallValues = true)
    {
        if (px == null) return;

        // 2026-09-22 用户口径：**图上所有标注统一用黑色**（大球的环/叉、炮塔的点、朝向线与箭头、文字），
        // 阵营不再靠颜色区分 —— 阵营写进标签文字里（"你的 / 赤喵的 / 中立的"）。文字不要背景块。
        Color32 ink = new Color32(0, 0, 0, 255);
        Color32 noBg = new Color32(0, 0, 0, 0);

        int scale = size >= 768 ? 3 : (size >= 384 ? 2 : 1);   // 512 图 -> 每格 2px，字高 14px
        float perPixel = (viewHalf * 2f) / size;               // 整张地图铺满这张图

        // 可见判据与 BuildLabels 完全同一个（实体级：中心点或它自己身上有一点可见）——
        // 用户 2026-09-22："怎么出现了一个在领土里但是没有标注的穿甲"。
        bool InSight(Vector2 p, float r = 0f) => EntityVisible(sightCenter, sightRadius, myStage, p, r);

        // 世界坐标 -> 网格像素（y 向上为正，和 GetPixels32 下标一致）
        bool TryToPixel(Vector2 world, out int gx, out int gy)
        {
            gx = Mathf.RoundToInt((world.x - (viewCenter.x - viewHalf)) / perPixel);
            gy = Mathf.RoundToInt((world.y - (viewCenter.y - viewHalf)) / perPixel);
            return gx >= 0 && gy >= 0 && gx < size && gy < size;
        }

        void DrawValue(Vector2 world, string text, Color32 fill, int markerRadius, int lift)
        {
            text = PixelDraw.Filter(text);
            if (text.Length == 0) return;
            if (!TryToPixel(world, out int gx, out int gy)) return;

            // 数值写在标记**上方**（留出 lift 像素的空档），并且夹在网格里，别被裁掉。
            // 只在"标签建不出来"的兜底路径用；黑色字、**不要背景块**。
            int tw = PixelDraw.TextWidth(text, scale);
            int tx = Mathf.Clamp(gx - tw / 2, 1, Mathf.Max(1, size - tw - 1));
            int ty = Mathf.Clamp(gy + markerRadius + lift, 1, size - PixelDraw.TextHeight(scale) - 2 * scale - 1);
            PixelDraw.DrawText(px, size, tx, ty, text, scale, fill, noBg);
        }

        int maxEntities = 40;   // 视野里几十个弹体已经很多了，别把图糊满
        int drawn = 0;

        BallPainter[] balls = UnityEngine.Object.FindObjectsOfType<BallPainter>();
        foreach (BallPainter bp in balls)
        {
            if (drawn >= maxEntities) break;
            if (bp == null || bp.value <= 0) continue;      // 已经归零的就不标了

            Vector2 pos = bp.transform.position;
            if (!InSight(pos, bp.transform.lossyScale.x * 0.5f)) continue;
            if (!TryToPixel(pos, out int gx, out int gy)) continue;

            bool shell = bp.game_item_name == "穿甲";
            int markerR = shell ? 3 : Mathf.Clamp(Mathf.RoundToInt(bp.transform.lossyScale.x / perPixel * 0.5f), 3, 26);
            Color32 edge = ink;

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

            if (drawBallValues) DrawValue(pos, bp.value.ToShortString(), ink, markerR, 3);
            drawn++;
        }

        // 炮塔只画位置，**一律不标数值**（自己的子弹量情报正文里有；别人的子弹量是私有情报）。
        // 2026-09-22：圆外的不画了（原来只判"在不在图上"，圆外照样画 —— 用户："红色的炮塔都不在视野里还标出来？"），
        // 标记也统一黑色；是谁由标签文字写（"你的炮塔 / 青昔的炮塔"）。
        for (int i = 1; i < 5; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;

            Vector2 pos = t.transform.position;
            if (!InSight(pos, 0.45f)) continue;
            if (!TryToPixel(pos, out int gx, out int gy)) continue;

            PixelDraw.DrawDisc(px, size, gx, gy, Mathf.Max(2, scale * 2), ink);
            PixelDraw.DrawRing(px, size, gx, gy, Mathf.Max(5, scale * 3 + 2), Mathf.Max(1, scale), ink);
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
    private static List<SightLabel> BuildLabels(Vector2 viewCenter, float viewHalf, Vector2 sightCenter, float sightRadius, int myStage)
    {
        var list = new List<SightLabel>();

        // 标注判据：**实体**在可见范围内（中心点，或它自己身上有一点可见 —— 见 EntityVisible 的注释）——
        // 敌人和自己的炮塔都算（自己那台写"你的炮塔"），看不见的一个都不标。
        bool InView(Vector2 p, float r = 0f) =>
            EntityVisible(sightCenter, sightRadius, myStage, p, r) &&
            Mathf.Abs(p.x - viewCenter.x) <= viewHalf && Mathf.Abs(p.y - viewCenter.y) <= viewHalf;

        string Coord(Vector2 p) => "(" + p.x.ToString("0.0") + "," + p.y.ToString("0.0") + ")";

        // 炮塔：阵营 + 类别 + 位置（炮塔没有速度，也不写数值）
        for (int i = 1; i < 5; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;
            Vector2 p = t.transform.position;
            if (!InView(p, 0.45f)) continue;

            bool mine = i == myStage;
            list.Add(new SightLabel
            {
                text = (mine ? "你的" : AIAgent.GetStageName(i) + "的") + "炮塔" + Coord(p),
                at = p,
                radius = 0.45f,
                color = LabelInkColor,
            });
        }

        // 大球 / 穿甲弹：**一条标签写完五样**（阵营、类别、数值、位置、速度）——
        // 用户 2026-09-22："合并为一个包含：大球数值，位置，速度，阵营，类别的黑色字，不要背景"。
        var notLabeled = new List<string>();
        foreach (BallPainter bp in UnityEngine.Object.FindObjectsOfType<BallPainter>())
        {
            if (bp == null) continue;
            Vector2 p = bp.transform.position;
            if (bp.value <= 0) { notLabeled.Add($"{bp.game_item_name}{Coord(p)} 数值已 0"); continue; }
            float ownR = Mathf.Max(0.25f, bp.transform.lossyScale.x * 0.5f);
            if (!InView(p, ownR))
            {
                notLabeled.Add($"{bp.game_item_name}{Coord(p)} 不在可见范围内");
                continue;
            }

            bool mine = bp.stage == myStage;
            string owner = mine ? "你的" : (bp.stage == 0 ? "中立的" : AIAgent.GetStageName(bp.stage) + "的");
            Vector2 v = bp.rb != null ? bp.rb.velocity : Vector2.zero;
            string speed = v.magnitude > 0.05f
                ? " 速" + v.magnitude.ToString("0.0") + InformGetter.CardinalDirection(v)
                : "";
            list.Add(new SightLabel
            {
                text = owner + bp.game_item_name + bp.value.ToShortString() + Coord(p) + speed,
                at = p,
                radius = ownR,
                color = LabelInkColor,
            });
        }

        // 有球没被标上就把原因打出来（用户 2026-09-22："怎么出现了一个在领土里但是没有标注的穿甲"）——
        // 下次再出现这种情况，日志直接说是"数值已 0"还是"不在可见范围内"。
        if (notLabeled.Count > 0)
            Debug.Log($"[移动截图] 有 {notLabeled.Count} 个球没标：{string.Join("；", notLabeled.GetRange(0, Mathf.Min(4, notLabeled.Count)))}");

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

            // 整棵子树都放到标签专用层：第二遍渲染用它当 cullingMask，只会拍到标签
            foreach (Transform tr in go.GetComponentsInChildren<Transform>(true))
                if (tr != null) tr.gameObject.layer = LabelLayer;

            tmp.enableAutoSizing = false;
            tmp.fontSize = 32f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.color = color;
            tmp.text = text;
            if (tmp.rectTransform != null) tmp.rectTransform.sizeDelta = new Vector2(800f, 120f);

            // 黑字 + **白描边**（2026-09-22 用户："TM的你的字本来就不清楚啊！（a）"）：
            // 纯黑字压在那片"地图之外"的暗红/深蓝上根本看不见，而用户又要"不要背景块"——
            // 所以走的还是"描边"这条老路（DisplayValue.SetOutline 同款做法），只是描边改白色。
            // 注意是**材质实例**，用完必须销毁（记在 labelMaterials 里，DestroyLabels 一起清）。
            Material baseMat = tmp.fontSharedMaterial != null ? tmp.fontSharedMaterial
                            : (tmp.font != null ? tmp.font.material : null);
            if (baseMat != null)
            {
                var mat = new Material(baseMat);
                mat.SetColor("_OutlineColor", Color.white);
                mat.SetFloat("_OutlineWidth", LabelOutlineWidth);
                mat.EnableKeyword("OUTLINE_ON");
                tmp.fontMaterial = mat;
                labelMaterials.Add(mat);
            }

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
    private static List<GameObject> SpawnLabels(List<SightLabel> items, int size, float viewHalf, Vector2 viewCenter)
    {
        var spawned = new List<GameObject>();
        if (items == null || items.Count == 0) return spawned;

        float perPixel = (viewHalf * 2f) / size;               // 整张地图铺满这张图
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
            float px = Mathf.Clamp((lb.at.x - (viewCenter.x - viewHalf)) / perPixel, halfW + 1f, size - halfW - 1f);
            float py = (lb.at.y - (viewCenter.y - viewHalf)) / perPixel;

            float bottom = py + Mathf.Max(2f, lb.radius / perPixel) + LabelGap / perPixel;
            float stepY = halfH * 2f + 2f;
            float stepX = halfW + 4f;

            // 2026-09-22 用户："不要TM的一个标注压另一个标注啊" ——
            // 原来只往上让 12 次、让不动也照画（于是叠在一起）。现在按候选位逐个试：
            // 正上方逐格往上 → 左右挪 → 往下；要求**不和任何已有标签重叠**、不越出图；
            // 全都不行就**这一条不画**。（标签是抹黑之后单独合成上去的，所以不需要再躲"视野圆外"那圈黑区。）
            Rect rect = default;
            bool placed = false;
            for (int c = 0; c < 24 && !placed; c++)
            {
                float ox, oy;
                if (c < 10) { ox = 0f; oy = c; }
                else if (c < 16) { int k = 1 + (c - 10) / 2; ox = (c % 2 == 0) ? -k : k; oy = 0f; }
                else { ox = 0f; oy = -(1f + (c - 16)); }

                var cand = new Rect(px + ox * stepX - halfW, bottom + oy * stepY, halfW * 2f, halfH * 2f);
                if (cand.xMin < 1f || cand.xMax > size - 1f) continue;
                if (cand.yMin < 1f || cand.yMax > size - 1f) continue;

                bool hit = false;
                foreach (Rect r in used)
                    if (r.Overlaps(cand)) { hit = true; break; }
                if (hit) continue;

                rect = cand;
                placed = true;
            }
            if (!placed) continue;          // 实在放不下：宁可不标，也不叠上去
            used.Add(rect);

            float wx = viewCenter.x - viewHalf + px * perPixel;
            float wy = viewCenter.y - viewHalf + (rect.y + halfH) * perPixel;
            go.transform.position = new Vector3(wx, wy, -2f);       // z 拉到最前，压在地图之上

            spawned.Add(go);
        }

        return spawned;
    }

    /// <summary>渲染完立刻销毁这次建的标签（连同它们的描边材质实例，不留任何残留物在场景里）。</summary>
    private static void DestroyLabels(List<GameObject> labels)
    {
        foreach (Material m in labelMaterials)
            if (m != null) UnityEngine.Object.Destroy(m);
        labelMaterials.Clear();

        if (labels == null) return;
        foreach (GameObject go in labels)
        {
            if (go == null) continue;
            // 先 SetActive(false) 再 Destroy：Destroy 要到帧末才真正生效，
            // 中间这一帧主相机还会把它们画到游戏画面里（会闪一下字）。
            go.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }
        labels.Clear();
    }

    #endregion

    /// <summary>存盘（查 bug 用）。文件名带 tag/阵营/坐标/半径/时间，方便对照。</summary>
    private static string Save(byte[] png, int stage, Vector2 pos, float radius, float maxMove, string tag = "")
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, FolderName);
            Directory.CreateDirectory(dir);
            string prefix = string.IsNullOrWhiteSpace(tag) ? "" : tag.Trim() + "_";
            string name = $"{prefix}move_s{stage}_{DateTime.Now:yyyyMMdd_HHmmss_fff}" +
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
