using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponsDisplayer : MonoBehaviour
{
    public float y = 0.445f;//偏移
    public GameObject item;//用于显示的物品
    public GameObject up;//用于显示的物品

    [Header("特效预览（仅视觉，勾选后运行，所有已解锁武器格按 previewValue 显示特效）")]
    public bool previewEffect;
    [Range(0, 40)] public int previewValue = 20;

    [Header("道具槽位前移动画时长(秒)")]
    public float moveDuration = 0.25f;

    // 每个阵营的锁定槽显示实例（固定槽位，只负责"锁定+倒计时"，绝不显示道具）
    private readonly List<UIWeaponItem>[] campItems = new List<UIWeaponItem>[5];

    // 道具显示状态：显示实例跟着道具 id 走（不是槽位换皮）。
    // 使用道具后，被用的格子播消失动画，剩余道具的图标用位移动画滑到前一个槽，
    // 避免"同一道具同时出现在前后两个格子"的涂抹现象。
    // 道具实例是独立池（5 个/阵营），与锁定槽实例分开，两套显示互不抢占。
    private class CampPropUI
    {
        public readonly Dictionary<int, UIWeaponItem> byId = new();   // 道具id -> 显示实例
        public readonly Dictionary<UIWeaponItem, int> slots = new();  // 实例 -> 当前所在槽位
        public readonly List<UIWeaponItem> pool = new();              // 道具显示实例池（独立于锁定槽）
    }
    private readonly CampPropUI[] campPropUIs = new CampPropUI[5];

    // 每槽位解锁时间(秒)，对应 propLimit 前 N 个槽位；开局解锁 2 个(0,1)，之后 1/5/7 分钟各解锁 1 个
    public List<int> unlockTimes = new(){ -1, -1, 60, 300, 420 };

    private const float refreshInterval = 0.5f;    // 节流：倒计时/解锁 0.1s 刷一次即可
    private float refreshTimer;
    private bool refreshDirty;

    private Vector3 GetPos(int camp, int num)
    {
        Vector3 pos = transform.GetChild(camp - 1).GetChild(num).position;
        pos.y += camp > 2 ? y : -y;
        return pos;
    }

    private MapConfig config;
    private void Start()
    {
        config = MapConfig.Instance;

        // 预览开关：只影响武器格最外层 sp 的特效，不参与任何游戏数值逻辑。
        UIWeaponItem.PreviewOverride = previewEffect;
        UIWeaponItem.PreviewOverrideValue = previewValue;

        for (int i = 1; i < 5; i++)
        {
            campItems[i] = new List<UIWeaponItem>();
            campPropUIs[i] = new CampPropUI();

            // 锁定槽实例：固定槽位，只显示锁定与倒计时
            for (int a = 0; a < 5; a++)
            {
                GameObject go = Instantiate(item, transform);
                GameObject upg = Instantiate(up, transform);
                upg.transform.position = go.transform.position = GetPos(i, a);
                go.SetActive(false);   // 初始隐藏，之后由 Show/Hide 驱动
                campItems[i].Add(go.GetComponent<UIWeaponItem>());
            }

            // 道具显示实例池：独立 5 个浮动实例，初始全隐藏，SetData 时摆到目标槽
            for (int p = 0; p < 5; p++)
            {
                GameObject go = Instantiate(item, transform);
                go.SetActive(false);
                campPropUIs[i].pool.Add(go.GetComponent<UIWeaponItem>());
            }
        }

        MapConfig.OnPropIn += OnPropChanged;
        MapConfig.OnPropOut += OnPropChanged;

        RefreshAll();
    }

    private void OnDestroy()
    {
        MapConfig.OnPropIn -= OnPropChanged;
        MapConfig.OnPropOut -= OnPropChanged;
    }

    private void Update()
    {
        refreshTimer += Time.deltaTime;
        bool due = refreshTimer >= refreshInterval;
        if (due) refreshTimer = 0f;
        if (due || refreshDirty)
        {
            refreshDirty = false;
            RefreshAll();   // OnPropIn/Out 合并到同一帧刷新，避免中间槽位假消失动画
        }
    }

    private void OnPropChanged(PropInfo info) => refreshDirty = true;   // 閬撳叿杩涘嚭鏍堝悎骞跺埌鍚屼竴甯у埛鏂?

    private void RefreshAll()
    {
        do
        {
            if (unlockTimes.Count == 0) break;
            if (Time.time > unlockTimes[0])
            {
                //解锁
                config.propLimit++;
                unlockTimes.RemoveAt(0);
            }
            else break;
        } while (true);

        for (int camp = 1; camp <= 4; camp++) RefreshCamp(camp);
    }

    private void RefreshCamp(int camp)
    {
        var props = config.teamProps[camp];
        var list = campItems[camp];
        if (list == null) return;

        // 锁定槽：固定实例显示"锁定+倒计时"；道具槽的锁定实例隐藏（道具由独立池实例负责显示）
        for (int a = 0; a < list.Count; a++)
        {
            UIWeaponItem wc = list[a];
            if (a >= config.propLimit)
            {
                wc.SetLocked(FormatRemain(unlockTimes[a - list.Count + unlockTimes.Count] - Time.time));
            }
            else if (wc.gameObject.activeSelf)
            {
                wc.ForceHideNow();   // 槽位解锁：隐藏锁定实例，空出该槽给道具
            }
        }

        RefreshCampProps(camp, props);
    }

    /// <summary>
    /// 道具槽对账：以 props 当前列表为准。
    /// 1. 已显示但道具不在列表（被使用/溢出）→ 原地播消失动画；
    /// 2. 道具还在但槽位变化 → 位移动画滑到目标槽；
    /// 3. 新道具还没有显示实例 → 从池取空闲实例，摆到目标槽播入场动画。
    /// </summary>
    private void RefreshCampProps(int camp, List<PropEntry> props)
    {
        CampPropUI ui = campPropUIs[camp];
        if (ui == null) return;
        Color campColor = config.GetColor(camp, MapConfig.ColorStage.Dark);

        HashSet<int> targetIds = new();
        for (int a = 0; a < props.Count; a++) targetIds.Add(props[a].id);

        // 1. 消失
        foreach (var kv in new List<KeyValuePair<int, UIWeaponItem>>(ui.byId))
        {
            if (targetIds.Contains(kv.Key)) continue;
            ui.byId.Remove(kv.Key);
            ui.slots.Remove(kv.Value);
            kv.Value.Hide();
        }

        // 2. 位移
        for (int a = 0; a < props.Count; a++)
        {
            if (!ui.byId.TryGetValue(props[a].id, out UIWeaponItem wc)) continue;
            if (ui.slots.TryGetValue(wc, out int cur) && cur != a)
            {
                wc.MoveTo(GetPos(camp, a), moveDuration);
                ui.slots[wc] = a;
            }
        }

        // 3. 新道具
        for (int a = 0; a < props.Count; a++)
        {
            if (ui.byId.ContainsKey(props[a].id)) continue;
            UIWeaponItem wc = TakeIdleItem(ui);
            if (wc == null) continue;

            PropEntry p = props[a];
            wc.transform.position = GetPos(camp, a);
            wc.sp.color = campColor;
            wc.SetData(p.item, p.value);
            ui.byId[p.id] = wc;
            ui.slots[wc] = a;
        }
    }

    private UIWeaponItem TakeIdleItem(CampPropUI ui)
    {
        foreach (UIWeaponItem wc in ui.pool)
            if (wc.IsIdle) return wc;

        // 都在动画中：优先强制停掉一个缩回中的（它已退出 byId，安全；移动中的实例打断会造成错位）
        foreach (UIWeaponItem wc in ui.pool)
        {
            if (!wc.Moving) { wc.ForceHideNow(); return wc; }
        }

        // 全在移动（实际不可达，道具满 5 个时新道具只会溢出不会入栈）：本轮放弃，下次对账再显示
        return null;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // 编辑模式即时记录预览参数；进入 Play 后由 Start 应用到生成的武器格。
        UIWeaponItem.PreviewOverride = previewEffect;
        UIWeaponItem.PreviewOverrideValue = previewValue;
    }
#endif

    // 剩余时间转 "分:秒"，如 0:59 / 5:00
    private static string FormatRemain(float s)
    {
        int seconds = (int)s;
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }
}
