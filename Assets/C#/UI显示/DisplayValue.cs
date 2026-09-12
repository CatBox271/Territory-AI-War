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
    /// <summary>无敌倒计时的前缀（只有护盾数字会用到）</summary>
    public string invincibleLabel = "无敌时间: ";

    private IStageValue stageValue;
    /// <summary>aim 是塔的护盾（挂了 ShieldEffect）时才非空：护盾碎后的无敌窗口内改显示倒计时</summary>
    private Towel shieldTowel;
    private HugeInt last = -1;
    private StringBuilder sb = new();
    private char[] buf = new char[256];
    private int skip;
    private Material outlineMaterial;

    void Start()
    {
        if (aim != null)
        {
            aim.TryGetComponent(out stageValue);
            if (aim.TryGetComponent(out ShieldEffect _))
                shieldTowel = aim.GetComponentInParent<Towel>();
        }
        if (text == null) TryGetComponent(out text);
        if (text != null && outlineWidth > 0)
        {
            var mat = new Material(text.fontSharedMaterial);
            outlineMaterial = mat;
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
        outlineMaterial = mat;
        text.fontMaterial = mat;
        mat.SetColor("_OutlineColor", color);
        mat.SetFloat("_OutlineWidth", width);
        if (width > 0) mat.EnableKeyword("OUTLINE_ON");
        else mat.DisableKeyword("OUTLINE_ON");
    }

    void Update()
    {
        // 无敌窗口内：这个数字改成显示剩余无敌时间，窗口一结束就自动回到正常数值显示
        if (shieldTowel != null && shieldTowel.IsInvincible)
        {
            skip = 0;
            last = -1; // 让窗口结束后的第一帧必定重写一次正常数值
            SetText(invincibleLabel + Mathf.CeilToInt(shieldTowel.InvincibleRemaining));
            return;
        }

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

    void SetText(string s)
    {
        if (text == null || text.text == s) return;
        text.text = s;
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

        return new string(buf, 0, totalLen);
    }


    void OnDestroy()
    {
        if (outlineMaterial != null)
        {
            if (Application.isPlaying) Destroy(outlineMaterial);
            else DestroyImmediate(outlineMaterial);
            outlineMaterial = null;
        }
    }
}
