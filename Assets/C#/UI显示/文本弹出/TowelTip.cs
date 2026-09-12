using TMPro;
using UnityEngine;

/// <summary>
/// ShowTMP 上的显示逻辑：不做逐字飞入，整句直接出现。
/// 出现时停在指定位置（炮塔），从 startScale 缩放展开到正常大小；
/// 随后缓慢把 y 移到 yTarget（默认 0 = 场地中线），只动 y，x/z 不变；
/// 停留 holdTime 后在 fadeTime 内淡出，最后销毁自己。
/// 所有节奏参数都是 public，直接在 Inspector 里调。
/// 一条实例只播一条文案；同一座炮塔上的多条由 Towel 各建一份、用 SetStackOffset 上下排开。
/// </summary>
[RequireComponent(typeof(TextMeshPro))]
public class TowelTip : MonoBehaviour
{
    [Header("出现：缩放")]
    [Tooltip("出现瞬间相对正常缩放的倍率")]
    public float startScale = 0.15f;
    [Tooltip("从 startScale 放大到正常缩放所用的时间（秒）")]
    public float scaleUpTime = 0.22f;
    [Tooltip("放大阶段的过冲幅度：0 = 不过冲，0.25 = 冲过正常大小再回弹")]
    public float scaleOvershoot = 0.25f;
    [Tooltip("缩放曲线（0→1）。留空则线性")]
    public AnimationCurve scaleCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 3f),
        new Keyframe(1f, 1f, 0f, 0f));

    [Header("移动：y 轴漂移")]
    [Tooltip("漂移目标的 y（世界坐标）。0 = 场地中线")]
    public float yTarget = 0f;
    [Tooltip("每秒向目标移动多少单位")]
    public float driftSpeed = 1.6f;
    [Tooltip("缩放完成后延迟多久才开始漂移（秒）")]
    public float driftDelay = 0.1f;

    [Header("停留与淡出")]
    [Tooltip("缩放完成后停留多久开始淡出（秒）")]
    public float holdTime = 2.2f;
    [Tooltip("淡出用时（秒）")]
    public float fadeTime = 0.5f;
    [Tooltip("播完是否销毁自己")]
    public bool destroyOnFinish = true;

    private TextMeshPro text;
    private Vector3 normalScale;
    private bool normalScaleCaptured;
    private float life;
    private bool playing;
    private float stackOffset;
    private float baseY;

    /// <summary>缩放阶段时长（秒）</summary>
    private float scaleDuration => Mathf.Max(0f, scaleUpTime);
    /// <summary>开始淡出的时间点（秒）</summary>
    private float fadeBegin => scaleDuration + Mathf.Max(0f, holdTime);
    /// <summary>整段动画的总时长（秒）</summary>
    private float lifetime => fadeBegin + Mathf.Max(0f, fadeTime);

    /// <summary>是否还在播放（播完的实例为 false，Towel 靠它腾出占用位置）</summary>
    public bool IsPlaying => playing;

    /// <summary>
    /// 这条飘字按正常缩放渲染时占的世界高度（量不到时返回 0）。
    /// 用来给同一座炮塔上同时存在的多条飘字在竖直方向错开。
    /// </summary>
    public float WorldHeight
    {
        get
        {
            if (text == null) return 0f;
            float parentY = transform.parent != null ? Mathf.Abs(transform.parent.lossyScale.y) : 1f;
            return Mathf.Abs(normalScale.y) * parentY * text.textBounds.size.y;
        }
    }

    void Awake()
    {
        CaptureNormalScale();
    }

    private void CaptureNormalScale()
    {
        if (text == null) text = GetComponent<TextMeshPro>();
        if (normalScaleCaptured) return;
        normalScale = transform.localScale;
        if (normalScale == Vector3.zero) normalScale = Vector3.one;
        normalScaleCaptured = true;
    }

    /// <summary>在 worldPos 处直接显示 content。一条实例只该被 Play 一次（每条飘字各建一份实例）。</summary>
    public void Play(string content, Color color, Vector3 worldPos)
    {
        CaptureNormalScale();

        stackOffset = 0f;
        baseY = worldPos.y;
        transform.position = worldPos;
        transform.localScale = normalScale * Mathf.Max(0.0001f, startScale);

        if (text != null)
        {
            text.text = AIAgent.ColorizeAINames(content ?? string.Empty);
            text.color = color;
            text.alpha = 1f;
        }

        life = 0f;
        playing = true;
        if (!gameObject.activeSelf) gameObject.SetActive(true);

        // 立刻更新一次网格，textBounds 才是这条文案的（WorldHeight 在同一帧就能量到）
        if (text != null) text.ForceMeshUpdate();
    }

    /// <summary>
    /// 在 Play 之后调用：把整条轨迹（出现位置 + 漂移终点）沿 y 挪开 yOffset，
    /// 让同一座炮塔上同时存在的多条飘字上下排开、互不遮挡。
    /// </summary>
    public void SetStackOffset(float yOffset)
    {
        stackOffset = yOffset;
        Vector3 p = transform.position;
        p.y = baseY + stackOffset;
        transform.position = p;
    }

    void Update()
    {
        if (!playing) return;
        life += Time.deltaTime;

        // 1) 缩放：startScale → 正常大小
        float k = scaleDuration <= 0f ? 1f : Mathf.Clamp01(life / scaleDuration);
        float eased = scaleCurve != null && scaleCurve.length > 0 ? scaleCurve.Evaluate(k) : k;
        eased += scaleOvershoot * Mathf.Sin(k * Mathf.PI);
        transform.localScale = normalScale * Mathf.LerpUnclamped(startScale, 1f, eased);

        // 2) 漂移：只动 y，向 yTarget（+ 本条自己的错开量）缓慢靠近
        if (life >= driftDelay && driftSpeed > 0f)
        {
            Vector3 p = transform.position;
            p.y = Mathf.MoveTowards(p.y, yTarget + stackOffset, driftSpeed * Time.deltaTime);
            transform.position = p;
        }

        // 3) 停留结束 → 淡出
        if (life > fadeBegin)
        {
            float remain = Mathf.Max(0f, fadeTime);
            float a = remain <= 0f ? 0f : 1f - Mathf.Clamp01((life - fadeBegin) / remain);
            if (text != null) text.alpha = a;
        }

        // 4) 结束
        if (life >= lifetime)
        {
            playing = false;
            if (destroyOnFinish) Destroy(gameObject);
        }
    }
}
