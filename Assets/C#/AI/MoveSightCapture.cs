using System;
using System.IO;
using TMPro;
using UnityEngine;

/// <summary>
/// 移动前的「炮塔视野截图」：以炮塔为圆心、半径 = 0.6 × 当前最大移动距离 的圆形俯视图。
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
    /// <summary>半径倍率：半径 = 这个值 × 炮塔当前最大移动距离。</summary>
    public static float RadiusFactor = 0.6f;
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

    /// <summary>面板上的开关 / 倍率 / 分辨率优先（在 AIAgent 上调），没挂 AIAgent 时用这里的静态默认值。</summary>
    private static void SyncFromAgent()
    {
        AIAgent agent = AIAgent.Instance;
        if (agent == null) return;
        Enabled = agent.moveSightEnabled;
        RadiusFactor = agent.moveSightRadiusFactor;
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

        shot.png = tex.EncodeToPNG();
        UnityEngine.Object.Destroy(tex);

        if (shot.png == null || shot.png.Length == 0) { shot.error = "PNG 编码失败"; return shot; }

        shot.dataUrl = "data:image/png;base64," + Convert.ToBase64String(shot.png);
        shot.savedPath = Save(shot.png, stage, center, radius, maxMove);
        shot.ok = true;
        return shot;
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
