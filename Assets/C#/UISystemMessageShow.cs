using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 操作横幅上的 3D 滚动消息显示。
/// 三个 TextMeshPro 的槽位语义：
///   1 = 正式显示（满 alpha，y_scale=1）
///   2 = 半透明、y_scale 缩小、z 在最后面
///   3 = 文字向上滚出后完全透明的槽位
/// 新消息进入 2 位时：先瞬移到 1 位的 y（alpha=0、z 靠后、y_scale 比 2 更小），
/// 再线性向下滚到 2 位。
/// 1 位上的消息显示 holdDuration 后自动向上滚到 3 位消失。
/// </summary>
public class UISystemMessageShow : MonoBehaviour
{
    public static UISystemMessageShow Instance;

    public bool test;

    private void Update()
    {
        if (test)
        {
            test = false;
            ShowMessage(Time.time.ToString());
        }
    }

    [Header("槽位外观")]
    [Range(0f, 1f)] public float alphaSlot1 = 1f;
    [Range(0f, 1f)] public float alphaSlot2 = 0.5f;
    [Range(0f, 1f)] public float alphaSlot3 = 0f;

    public float scaleYSlot1 = 1f;
    public float scaleYSlot2 = 0.8f;
    public float scaleYSlot3 = 0.6f;

    public float zSlot1 = 0f;
    public float zSlot2 = -1f;   // 槽位2/进入动画的 z 在最后面
    public float zSlot3 = 0f;

    [Header("动画")]
    public float scrollDuration = 0.4f;   // 每段滚动时长
    public float holdDuration = 2f;       // 1 位正式显示时长

    private readonly TextMeshPro[] texts = new TextMeshPro[3];
    private readonly RectTransform[] rects = new RectTransform[3];
    private readonly Color[] baseColors = new Color[3];
    private readonly Vector3[] baseScales = new Vector3[3];

    private readonly SlotPose[] slotPoses = new SlotPose[3];
    private readonly int[] atSlot = new int[3];  // slot -> text 下标

    private readonly Queue<string> queue = new Queue<string>();
    private Coroutine worker;
    private int visibleCount;
    private float slot1Since;

    private const int S1 = 0;
    private const int S2 = 1;
    private const int S3 = 2;

    struct SlotPose
    {
        public float y;
        public float alpha;
        public float scaleY;
        public float z;
    }

    void Awake()
    {
        Instance = this;
        slot1Since = Time.time;

        for (int i = 0; i < 3; i++)
        {
            // 优先按名字找 1/2/3，避免用户调整层级顺序后错位
            Transform child = transform.Find((i + 1).ToString());
            if (child == null) child = transform.GetChild(i);

            texts[i] = child.GetComponent<TextMeshPro>();
            rects[i] = child.GetComponent<RectTransform>();
            baseColors[i] = texts[i] != null ? texts[i].color : Color.white;
            baseScales[i] = child.localScale;

            texts[i].text = "";
            atSlot[i] = i;
        }

        // 用场景里 1/2/3 的当前 y 作为三个槽位位置
        slotPoses[S1].y = rects[0].localPosition.y;
        slotPoses[S2].y = rects[1].localPosition.y;
        slotPoses[S3].y = rects[2].localPosition.y;

        slotPoses[S1].alpha = alphaSlot1;
        slotPoses[S1].scaleY = scaleYSlot1;
        slotPoses[S1].z = zSlot1;

        slotPoses[S2].alpha = alphaSlot2;
        slotPoses[S2].scaleY = scaleYSlot2;
        slotPoses[S2].z = zSlot2;

        slotPoses[S3].alpha = alphaSlot3;
        slotPoses[S3].scaleY = scaleYSlot3;
        slotPoses[S3].z = zSlot3;

        ApplySlot(0, slotPoses[S1]);
        ApplySlot(1, slotPoses[S2]);
        ApplySlot(2, slotPoses[S3]);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>显示一条系统消息。</summary>
    public void ShowMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        queue.Enqueue(AIAgent.ColorizeAINames(message));
        if (worker == null) worker = StartCoroutine(Worker());
    }

    public static void Show(string message)
    {
        if (Instance != null) Instance.ShowMessage(message);
    }

    /// <summary>立即显示：不走动画队列，直接把文字放到 1 位。用于死亡暂停时的遗言等必须马上可见的消息。</summary>
    public static void ShowNow(string message)
    {
        if (Instance != null) Instance.ShowMessageNow(message);
    }

    public void ShowMessageNow(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (texts == null || texts.Length < 3 || texts[S1] == null || texts[S2] == null || texts[S3] == null) return;
        if (rects == null || rects.Length < 3 || rects[S1] == null) return;

        int idx = atSlot[S1];
        texts[idx].text = AIAgent.ColorizeAINames(message);
        ApplySlot(idx, slotPoses[S1]);
        visibleCount = 1;
        slot1Since = Time.time;
    }

    IEnumerator Worker()
    {
        while (queue.Count > 0 || visibleCount > 0)
        {
            if (queue.Count > 0)
            {
                string message = queue.Dequeue();

                // 1 位正在正式显示唯一一条消息：新消息只走 3->2，不顶掉 1
                if (visibleCount == 1 && !string.IsNullOrEmpty(texts[atSlot[S1]].text))
                {
                    yield return EnterSlot2(message);
                }
                else
                {
                    yield return NormalCycle(message);
                }
            }
            else if (visibleCount == 1 && string.IsNullOrEmpty(texts[atSlot[S1]].text))
            {
                // 唯一消息还停在 2 位：先滚到 1 位，才开始计算正式显示时间
                yield return PromoteOnly();
            }
            else if (visibleCount > 0)
            {
                // 1 位有消息：显示 holdDuration 后自动向上滚出
                yield return HoldThenTimeout();
            }
            else
            {
                yield return null;
            }
        }
        worker = null;
    }

    IEnumerator NormalCycle(string message)
    {
        int from1 = atSlot[S1];
        int from2 = atSlot[S2];
        int from3 = atSlot[S3];

        texts[from3].text = message;

        // 新消息进入 2 位：先瞬移到 1 的 y，alpha=0、z 靠后、y_scale 比 2 更小，再线性滚到 2
        SlotPose entryStart = EntryStartPose();
        ApplySlot(from3, entryStart);

        float t = 0f;
        while (t < scrollDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(scrollDuration, 0.0001f));

            ApplySlot(from1, Lerp(slotPoses[S1], slotPoses[S3], k)); // 1 向上滚出
            ApplySlot(from2, Lerp(slotPoses[S2], slotPoses[S1], k)); // 2 向上成为正式
            ApplySlot(from3, Lerp(entryStart, slotPoses[S2], k));     // 3 从 1 后面向下滚入
            yield return null;
        }

        ApplySlot(from1, slotPoses[S3]);
        ApplySlot(from2, slotPoses[S1]);
        ApplySlot(from3, slotPoses[S2]);

        atSlot[S3] = from1;
        atSlot[S1] = from2;
        atSlot[S2] = from3;

        if (!string.IsNullOrEmpty(texts[from1].text)) visibleCount--;
        texts[from1].text = ""; // 滚到 3 位完全透明后清掉
        visibleCount++;          // from3 带着新消息进入半透明槽位
        slot1Since = Time.time;  // from2 刚到 1 位，重新计时
    }

    IEnumerator EnterSlot2(string message)
    {
        int from2 = atSlot[S2];
        int from3 = atSlot[S3];

        texts[from3].text = message;

        // 原 2 位（空）直接放回 3 位；新消息瞬移到 1 的 y 后向下滚到 2
        ApplySlot(from2, slotPoses[S3]);

        SlotPose entryStart = EntryStartPose();
        ApplySlot(from3, entryStart);

        float t = 0f;
        while (t < scrollDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(scrollDuration, 0.0001f));
            ApplySlot(from3, Lerp(entryStart, slotPoses[S2], k));
            yield return null;
        }

        ApplySlot(from3, slotPoses[S2]);

        atSlot[S3] = from2;
        atSlot[S2] = from3;

        visibleCount++;
        // 不动 1 位，slot1Since 不重置
    }

    IEnumerator PromoteOnly()
    {
        yield return SwapSlots(S2, S1);
        slot1Since = Time.time; // 到 1 位才开始算正式显示时间
    }

    IEnumerator HoldThenTimeout()
    {
        while (Time.time - slot1Since < holdDuration)
        {
            if (queue.Count > 0) yield break; // 来新消息交给 Worker 处理
            yield return null;
        }

        // 1 位显示时长到了：自动向上滚出
        yield return TimeoutCycle();
    }

    IEnumerator TimeoutCycle()
    {
        int from1 = atSlot[S1];
        int from2 = atSlot[S2];
        int from3 = atSlot[S3];

        texts[from3].text = ""; // 防止旧字迹滚到 2 位露出来

        float t = 0f;
        while (t < scrollDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(scrollDuration, 0.0001f));

            ApplySlot(from1, Lerp(slotPoses[S1], slotPoses[S3], k));
            ApplySlot(from2, Lerp(slotPoses[S2], slotPoses[S1], k));
            ApplySlot(from3, Lerp(slotPoses[S3], slotPoses[S2], k));
            yield return null;
        }

        ApplySlot(from1, slotPoses[S3]);
        ApplySlot(from2, slotPoses[S1]);
        ApplySlot(from3, slotPoses[S2]);

        atSlot[S3] = from1;
        atSlot[S1] = from2;
        atSlot[S2] = from3;

        if (!string.IsNullOrEmpty(texts[from1].text)) visibleCount--;
        texts[from1].text = "";
        slot1Since = Time.time;
    }

    IEnumerator SwapSlots(int slotA, int slotB)
    {
        int idxA = atSlot[slotA];
        int idxB = atSlot[slotB];

        float t = 0f;
        while (t < scrollDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(scrollDuration, 0.0001f));

            ApplySlot(idxA, Lerp(slotPoses[slotA], slotPoses[slotB], k));
            ApplySlot(idxB, Lerp(slotPoses[slotB], slotPoses[slotA], k));
            yield return null;
        }

        ApplySlot(idxA, slotPoses[slotB]);
        ApplySlot(idxB, slotPoses[slotA]);

        atSlot[slotA] = idxB;
        atSlot[slotB] = idxA;
    }

    SlotPose EntryStartPose()
    {
        return new SlotPose
        {
            y = slotPoses[S1].y,        // 先瞬移到 1 的 y
            alpha = 0f,                 // 完全透明
            scaleY = slotPoses[S3].scaleY, // 比 2 还小
            z = slotPoses[S2].z         // z 靠后，小于 1、2
        };
    }

    void ApplySlot(int index, SlotPose pose)
    {
        RectTransform rt = rects[index];
        if (rt == null) return;

        // 位置只用本地坐标 localPosition，避免 anchoredPosition 换算抖动
        Vector3 lp = rt.localPosition;
        lp.y = pose.y;
        lp.z = pose.z;
        rt.localPosition = lp;


        Vector3 scale = baseScales[index];
        scale.y = baseScales[index].y * pose.scaleY;
        rt.localScale = scale;

        if (texts[index] != null)
        {
            Color color = baseColors[index];
            color.a = pose.alpha;
            texts[index].color = color;
        }
    }

    static SlotPose Lerp(SlotPose a, SlotPose b, float k)
    {
        return new SlotPose
        {
            y = Mathf.Lerp(a.y, b.y, k),
            alpha = Mathf.Lerp(a.alpha, b.alpha, k),
            scaleY = Mathf.Lerp(a.scaleY, b.scaleY, k),
            z = Mathf.Lerp(a.z, b.z, k)
        };
    }
}