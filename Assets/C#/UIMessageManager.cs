using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public struct UIMInfo
{
    public int stage;
    public string content;
    public SpriteEmotion emo;
    // true 时无视正文里的 [emo:xxx]，强制用 emo（赢家定格用）
    public bool forceEmo;
}

public class UIMessageManager : MonoBehaviour
{
    public static UIMessageManager Instance;
    public GameObject item;
    public Vector2 YRange = new();
    public Transform MessageParent;
    public int MessageCount;

    public float max_show_time = 7f;
    public float intensity = 4f;
    private float last_deal_time = 0;
    public List<UISprite> human = new();
    private Queue<UIMInfo> need_deal = new();
    private List<UIMessageBar> items = new();

    public bool test;
    private void Awake()
    {
        Instance = this;
    }
    private void Start()
    {
        last_deal_time = -max_show_time;
        for (int i = 0; i < MessageCount + 1; i++)
        {
            GameObject go = Instantiate(item, MessageParent.transform);
            var mb = go.GetComponent<UIMessageBar>();
            mb.YRange = YRange;
            mb.MessageListCount = MessageCount;
            mb.SetLow("", Color.white);
            items.Add(mb);
        }
        Adjust(false);
    }

    private void Update()
    {
        if (test)
        {
            test = false;
            AddMessage(new() { stage = 2, content = "别来无恙啊，这次小鸟不好给你机会了。", emo = (SpriteEmotion)Random.Range(0,8) });
        }

        float last = Time.time - last_deal_time;
        float delay_time = 1f / ( need_deal.Count / intensity + 1) * max_show_time;
        if (last < delay_time) return;

        Unfocus(lihui_index);
        lihui_switch();

        if (need_deal.Count != 0)
        {
            Deal();
            last_deal_time = Time.time;
        }
    }

    private void Adjust(bool focus)
    {
        float deltaY = (YRange[1] - YRange[0]) / (MessageCount <= 1 ? 1 : MessageCount - 1);
        for (int i = 0; i < items.Count; i++)
        {
            var child = items[i].transform;
            var mb = child.GetComponent<UIMessageBar>();
            if (i == 0) mb.focus = focus;
            else mb.focus = false;

            child.position = MessageParent.transform.position + new Vector3(0, YRange[0] + i * deltaY);
            Vector3 b_size = mb.background.transform.localScale;
            b_size.y = deltaY / (child.transform.localScale.y == 0 ? 1 : child.transform.localScale.y);
            mb.background.transform.localScale = b_size;

            mb.Refresh();
        }
    }

    private void Unfocus(int i)
    {
        //取消聚焦
        var lihui = human[i];
        if (lihui.show) lihui.Act(false);
        items[0].focus = false;
        items[0].Refresh();
    }
    private int lihui_index = 1;
    private void lihui_switch()
    {
        lihui_index = lihui_index == 1 ? 0 : 1;
    }
    private void Deal()
    {
        var item = need_deal.Dequeue();
        // 文本里的 [emo:xxx] 决定立绘表情；没写标记时用 UIMInfo.emo（公开发言=origin，遗言=fail）
        // forceEmo 时（赢家）无视标记，强制盖掉模型自己写的表情
        string content = AIAgent.ExtractEmotion(item.content, out bool hasEmo, out SpriteEmotion emo);
        var lihui = human[lihui_index];
        lihui.Set(item.stage, item.forceEmo ? item.emo : (hasEmo ? emo : item.emo));
        lihui.Act(true);
        for (int i = 0; i < items.Count; i++)
        {
            var text_ui = items[i];
            if (i == items.Count - 1)
            {
                text_ui.SetLow(AIAgent.ColorizeAINames(content), MapConfig.Instance.GetColor(item.stage));
                items.RemoveAt(i);
                items.Insert(0,text_ui);
            }
            text_ui.UpPos();
        }
    }
    public void AddMessage(UIMInfo info)
    {
        need_deal.Enqueue(info);
    }
}
