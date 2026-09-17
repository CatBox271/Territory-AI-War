using UnityEngine;

/// <summary>
/// 往「移动视野截图」这张 Texture2D 上直接画东西用的小工具：不打字、不依赖 UI，
/// 用一张 5×7 的点阵字模把数字/单位画进像素里（所以画出来的东西一定在图片里，模型看得见）。
///
/// 目前用到的字符就是 HugeInt.ToShortString() 的取值集合：0-9 . - K M B T P。
/// 以后要加字符，就往 Glyphs 里补一张 5×7 点阵。
/// </summary>
public static class PixelDraw
{
    private const int GW = 5, GH = 7;

    /// <summary>字符 -> 7 行 × 5 列的点阵（'#' 为亮点，从**上**到下）。</summary>
    private static readonly System.Collections.Generic.Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." },
        ['-'] = new[] { ".....", ".....", ".....", "#####", ".....", ".....", "....." },
        ['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
        ['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" },
        ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
        ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
        ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
        ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
    };

    public static bool Supports(char c) => Glyphs.ContainsKey(c);

    /// <summary>把字符串里不认识的字符去掉（比如意外出现的空白或其它符号）。</summary>
    public static string Filter(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (Supports(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>一个字符占用的横向像素：5 列字身 + 1 列间隔，再各留 1 像素给描边。</summary>
    public static int Advance(int scale) => (GW + 1) * scale;

    /// <summary>整串文字的像素宽（含描边留白），用来居中。</summary>
    public static int TextWidth(string s, int scale) => string.IsNullOrEmpty(s) ? 0 : Filter(s).Length * Advance(scale);

    /// <summary>文字高度（像素，不含描边）。</summary>
    public static int TextHeight(int scale) => GH * scale;

    /// <summary>
    /// 把文字画到像素数组上。(x, y) 是**文字左下的网格坐标**（y 向上为正，和 GetPixels32 的下标一致）。
    ///
    /// 先用 bg 把整块文字区刷成实心底（这样压在圆环、色块上也读得清），再画字身。
    /// 每个字符左侧留 1 格给描边，所以相邻字符不会互相糊在一起。
    /// </summary>
    public static void DrawText(Color32[] px, int size, int x, int y, string text, int scale, Color32 fill, Color32 bg)
    {
        if (px == null || string.IsNullOrEmpty(text) || scale <= 0) return;
        text = Filter(text);
        if (text.Length == 0) return;

        // 底：比字身宽高各大一圈（左右各 1 格、上下各半格）
        FillRect(px, size, x, y - scale, TextWidth(text, scale), GH * scale + 2 * scale, bg, false);

        int penX = x;
        foreach (char c in text)
        {
            if (!Glyphs.TryGetValue(c, out string[] rows)) { penX += Advance(scale); continue; }

            for (int row = 0; row < GH; row++)
            {
                for (int col = 0; col < GW; col++)
                {
                    if (rows[row][col] != '#') continue;
                    // rows[0] 是最上面一行；网格 y 向上，所以第 row 行落在 y + (GH-1-row)
                    FillRect(px, size, penX + col * scale + scale, y + (GH - 1 - row) * scale, scale, scale, fill, false);
                }
            }
            penX += Advance(scale);
        }
    }

    /// <summary>画一个实心圆点（炮塔/护盾标记用）。</summary>
    public static void DrawDisc(Color32[] px, int size, int cx, int cy, int radius, Color32 color)
    {
        if (px == null || radius <= 0) return;
        int r2 = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                Set(px, size, cx + dx, cy + dy, color);
            }
        }
    }

    /// <summary>画一个圆环（护盾用）。</summary>
    public static void DrawRing(Color32[] px, int size, int cx, int cy, int radius, int thickness, Color32 color)
    {
        if (px == null || radius <= 0) return;
        int outer2 = radius * radius;
        int inner = Mathf.Max(0, radius - Mathf.Max(1, thickness));
        int inner2 = inner * inner;
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 > outer2 || d2 < inner2) continue;
                Set(px, size, cx + dx, cy + dy, color);
            }
        }
    }

    /// <summary>画一条有宽度的线段（大球/穿甲弹的朝向提示用）。</summary>
    public static void DrawLine(Color32[] px, int size, int x0, int y0, int x1, int y1, int thickness, Color32 color)
    {
        int steps = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) + 1;
        int half = Mathf.Max(0, thickness / 2);
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0f : (float)i / steps;
            int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t));
            int y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
            if (half == 0) Set(px, size, x, y, color);
            else FillRect(px, size, x - half, y - half, half * 2 + 1, half * 2 + 1, color, false);
        }
    }

    private static void FillRect(Color32[] px, int size, int x, int y, int w, int h, Color32 color, bool onlyIfEmpty)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                int px_ = x + i, py_ = y + j;
                if (px_ < 0 || py_ < 0 || px_ >= size || py_ >= size) continue;
                int idx = py_ * size + px_;
                if (onlyIfEmpty && px[idx].a > 0) continue;   // 描边不覆盖已经画好的内容
                px[idx] = color;
            }
    }

    private static void Set(Color32[] px, int size, int x, int y, Color32 color)
    {
        if (x < 0 || y < 0 || x >= size || y >= size) return;
        px[y * size + x] = color;
    }
}
