using System.Text;
using UnityEngine;
using TMPro;

public class DisplayValue : MonoBehaviour
{
    public GameObject aim;
    public TMP_Text text;
    public bool shortDisplay = true;
    public Color outlineColor = new Color(0, 0, 0, 0);
    public float outlineWidth = 0f;

    private IStageValue stageValue;
    private HugeInt last = -1;
    private StringBuilder sb = new();
    private char[] buf = new char[256];
    private int skip;

    void Start()
    {
        aim.TryGetComponent(out stageValue);
        if (text == null) TryGetComponent(out text);
        if (text != null && outlineWidth > 0)
        {
            var mat = new Material(text.fontSharedMaterial);
            text.fontMaterial = mat;
            mat.SetColor("_OutlineColor", outlineColor);
            mat.SetFloat("_OutlineWidth", outlineWidth);
            mat.EnableKeyword("OUTLINE_ON");
        }
        skip = Random.Range(0, 15);
    }

    public void SetOutline(Color color, float width)
    {
        if (text == null) TryGetComponent(out text);
        if (text == null) return;
        var mat = new Material(text.fontSharedMaterial);
        text.fontMaterial = mat;
        mat.SetColor("_OutlineColor", color);
        mat.SetFloat("_OutlineWidth", width);
        if (width > 0) mat.EnableKeyword("OUTLINE_ON");
        else mat.DisableKeyword("OUTLINE_ON");
    }

    void Update()
    {
        if (++skip < 15) return;
        skip = 0;

        if (stageValue == null || text == null) return;
        if (last == stageValue.value) return;
        last = stageValue.value;

        if (shortDisplay)
            text.text = stageValue.value.ToShortString();
        else
            text.text = ToDetail(stageValue.value);
    }

    string ToDetail(HugeInt value)
    {
        sb.Clear();
        value.ToStringBuilder(sb); // 此时 sb = "1234567890123456"（无反序号）

        int len = sb.Length;
        bool neg = len > 0 && sb[0] == '-';
        int digitStart = neg ? 1 : 0;
        int digitLen = len - digitStart;
        if (digitLen <= 3) return sb.ToString();

        // 计算输出的总长度：数字位 + 分隔符数
        int seps = (digitLen - 1) / 3;
        int totalLen = len + seps;
        if (totalLen > buf.Length) buf = new char[totalLen];
        int src = len - 1;
        int dst = totalLen - 1;
        int cnt = 0;
        int grp = 1;

        while (src >= digitStart)
        {
            buf[dst--] = sb[src--];
            if (++cnt == 3 && src >= digitStart)
            {
                cnt = 0;
                buf[dst--] = grp % 3 == 0 ? '\n' : '\'';
                grp++;
            }
        }
        if (neg) buf[0] = '-';

        return new string(buf);
    }
}
