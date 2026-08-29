using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class UIMarbleUpgrade : MonoBehaviour
{
    public static UIMarbleUpgrade Instance;

    private readonly List<SpriteRenderer> sps = new();
    private readonly List<TextMeshPro> texts = new();
    private readonly List<BaseShake> shaked = new();

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
    public Vector3 GetPos(int stage)
    {
        stage--;
        if (stage >= sps.Count || stage < 0) return new();
        return sps[stage].transform.position;
    }
    int pass = 0;
    public void Update()
    {
        pass++;
        if (pass <= 10) return;
        pass = 0;

        for (int i = 0; i < sps.Count; i++)
        {
            marble.TryGetUpgradeInfo(i + 1, out float progress, out float cost);
            texts[i].text = $"当   前:{(int)progress}\n下一级:{(int)cost}";
        }
    }
}
