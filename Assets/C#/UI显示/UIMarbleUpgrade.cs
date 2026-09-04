using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System.Text;

public class UIMarbleUpgrade : MonoBehaviour
{
    public static UIMarbleUpgrade Instance;

    private readonly List<SpriteRenderer> sps = new();
    private readonly List<TextMeshPro> texts = new();
    public readonly List<BaseShake> shaked = new();

    private const float UpgradeTextDuration = 2f;
    private readonly Dictionary<int, float> upgradeTextHoldUntil = new();

    private MarbleManager marble;
    private void Awake()
    {
        Instance = this;
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            Transform cc = child.GetChild(0);
            sps.Add(child.GetComponent<SpriteRenderer>());
            texts.Add(cc.GetComponent<TextMeshPro>());
            shaked.Add(cc.GetComponent<BaseShake>());
        }
    }
    private void Start()
    {
        marble = MarbleManager.Instance;
        for (int i = 0; i < sps.Count; i++)
        {
            sps[i].color = MapConfig.Instance.GetColor(i + 1, MapConfig.ColorStage.Dark);
            if (texts[i] != null) texts[i].color = MapConfig.Instance.GetText(i + 1);
        }
    }

    public void ShowUpgrade(int stage)
    {
        int index = stage - 1;
        if (index < 0 || index >= sps.Count) return;

        if (index < shaked.Count && shaked[index] != null)
        {
            shaked[index].ToShake(Vector2.zero, 0.5f, 1);
        }

        if (index < texts.Count && texts[index] != null)
        {
            texts[index].text = "升级！";
        }

        upgradeTextHoldUntil[stage] = Time.time + UpgradeTextDuration;
    }

    public Vector3 GetPos(int stage)
    {
        stage--;
        if (stage >= sps.Count || stage < 0) return new();
        return sps[stage].transform.position;
    }
    int pass = 0;
    StringBuilder builder;

    public void Update()
    {
        pass++;
        if (pass <= 10) return;
        pass = 0;

        for (int i = 0; i < sps.Count; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i+1,out Towel t) || t==null || t.isDead)
            {
                sps[i].gameObject.SetActive(false);
                continue;
            }

            if (upgradeTextHoldUntil.TryGetValue(i + 1, out float holdUntil))
            {
                if (Time.time < holdUntil) continue;
                upgradeTextHoldUntil.Remove(i + 1);
            }

            marble.TryGetUpgradeInfo(i + 1, out float progress, out float cost);

            builder = new();
            builder.Append("/");
            builder.Append((int)cost);
            int a = (builder.Length - 1) * 2 + 1;
            builder.Insert(0, (int)progress);
            while (builder.Length < a)
            {
                builder.Insert(0, "_");
            }

            texts[i].text = builder.ToString();
        }
    }
}
