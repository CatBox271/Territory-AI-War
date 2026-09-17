using System;
using System.IO;
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
        try
        {
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
        OverlayEntities(px, shot.size, center, radius);

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
    /// * 大球 / 穿甲弹：实心菱形标记 + 一条指向**速度方向**的短棒（穿甲弹的方向很关键）+ 上方数值
    /// * 自己的炮塔本体：圆点 + 数值（**不画护盾**）
    /// </summary>
    private static void OverlayEntities(Color32[] px, int size, Vector2 center, float radius)
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
            Color32 edge = bp.stage == 0 ? labelColor : (bp.stage == 1 ? enemyEdge : mineEdge);

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

            // 朝向短棒：从球心指向速度方向，长度按"这一帧能跑多远"放大到看得见
            Vector2 v = bp.rb != null ? bp.rb.velocity : Vector2.zero;
            if (v.sqrMagnitude > 0.0001f)
            {
                Vector2 dir = v.normalized;
                int len = Mathf.Clamp(Mathf.RoundToInt(v.magnitude / perPixel * 0.25f), markerR + 4, size / 4);
                int tipX = gx + Mathf.RoundToInt(dir.x * len);
                int tipY = gy + Mathf.RoundToInt(dir.y * len);
                PixelDraw.DrawLine(px, size, gx, gy, tipX, tipY, Mathf.Max(1, scale - 1), edge);
            }

            DrawValue(pos, bp.value.ToShortString(), labelColor, markerR, 3);
            drawn++;
        }

        // 自己的炮塔：位置 + 数值（不画护盾圆环）
        MapConfig cfg = MapConfig.Instance;
        for (int i = 1; i < 5; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i, out Towel t) || t == null || t.isDead) continue;

            Vector2 pos = t.transform.position;
            if (!TryToPixel(pos, out int gx, out int gy)) continue;

            Color32 color = cfg != null ? (Color32)cfg.GetColor(i, MapConfig.ColorStage.Towel) : labelColor;
            PixelDraw.DrawDisc(px, size, gx, gy, Mathf.Max(2, scale * 2), color);

            DrawValue(pos, t.value.ToShortString(), labelColor, Mathf.Max(4, scale * 3), 3);
        }
    }

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
