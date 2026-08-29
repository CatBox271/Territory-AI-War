using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponsDisplayer : MonoBehaviour
{
    public float y = 0.445f;//偏移
    public GameObject item;//用于显示的物品

    [Header("特效预览（仅视觉，勾选后运行，所有已解锁武器格按 previewValue 显示特效）")]
    public bool previewEffect;
    [Range(0, 40)] public int previewValue = 20;

    // 每个阵营实例化的武器格子，下标 1..4
    private readonly List<UIWeaponItem>[] campItems = new List<UIWeaponItem>[5];

    // 每槽位解锁时间(秒)，对应 propLimit 前 N 个槽位；开局解锁 2 个(0,1)，之后 1/5/7 分钟各解锁 1 个
    private  List<int> unlockTimes = new(){ -1, -1, 60, 300, 420 };

    private const float refreshInterval = 0.5f;    // 节流：倒计时/解锁 0.1s 刷一次即可
    private float refreshTimer;

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
            for (int a = 0; a < 5; a++)
            {
                GameObject go = Instantiate(item, transform);
                go.transform.position = GetPos(i, a);
                go.SetActive(false);   // 初始隐藏，之后由 Show/Hide 驱动
                campItems[i].Add(go.GetComponent<UIWeaponItem>());
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
        if (refreshTimer < refreshInterval) return;
        refreshTimer = 0f;
        RefreshAll();   // 推进倒计时 & 到点自动解锁
    }

    private void OnPropChanged(PropInfo info) => RefreshAll();   // 道具进出栈即时刷新

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
        int propCount = props.Count;
        var list = campItems[camp];
        if (list == null) return;

        Color campColor = config.GetColor(camp, MapConfig.ColorStage.Dark);

        for (int a = 0; a < list.Count; a++)
        {
            SpriteRenderer sr = list[a].sp;
            UIWeaponItem wc = list[a];

            if (a >= config.propLimit)
            {
                // 未解锁：锁定态，必须显示(靠倒计时体现进度)
                if (wc != null)
                {
                    wc.SetLocked(FormatRemain(unlockTimes[a - list.Count + unlockTimes.Count] - Time.time));
                }
                else wc.gameObject.SetActive(true);
            }
            else
            {
                // 已解锁
                if (a < propCount)
                {
                    // 有道具：显示
                    sr.color = campColor;
                    if (wc != null) wc.SetData(props[a].item, props[a].value);
                    else wc.gameObject.SetActive(true);
                }
                else
                {
                    // 已解锁但无道具：播放消失动画后隐藏
                    if (wc != null) wc.Hide();
                    else wc.gameObject.SetActive(false);
                }
            }
        }
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
