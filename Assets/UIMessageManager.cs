using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public struct UIMInfo
{
    public int stage;
    public string content;
    public string emo;
}

public class UIMessageManager : MonoBehaviour
{
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
    private void Start()
    {
        last_deal_time = -max_show_time;
        for (int i = 0; i < MessageCount + 1; i++)
        {
            GameObject go = Instantiate(item, MessageParent.transform);
            var mb = go.GetComponent<UIMessageBar>();
            mb.Set("", Color.white);
            items.Add(mb);
        }
        Adjust(false);
    }

    private void Update()
    {
        if (test)
        {
            test = false;
            AddMessage(new() { stage = 0, content = "这就不行了，真是杂鱼。", emo = "origin" });
        }

        float last = Time.time - last_deal_time;
        float delay_time = 1f / ( need_deal.Count / intensity + 1) * max_show_time;
        if (last < delay_time) return;
        if (need_deal.Count == 0)
        {
            Unfocus(lihui_index);
        }
        else
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
    }
    private int lihui_index = 1;
    private void lihui_switch()
    {
        lihui_index = lihui_index == 1 ? 0 : 1;
    }
    private void Deal()
    {
        var item = need_deal.Dequeue();


        Unfocus(lihui_index);
        lihui_switch();
        var lihui = human[lihui_index];

        lihui.Act(true);
    }
    private void AddMessage(UIMInfo info)
    {
        need_deal.Enqueue(info);
    }
}
