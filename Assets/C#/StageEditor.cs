using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class StageEditor : MonoBehaviour
{
    private IStageValue stageValue;
    public int stage;
    public string value;
    public bool AutoDestroy = true;

    private void Awake()
    {
        if (HugeInt.TryParse(value, out HugeInt a)) stageValue?.SetValue(stage, a);
    }
    void Start()
    {
        if (!TryGetComponent(out stageValue)) Destroy(this);
        else if(HugeInt.TryParse(value, out HugeInt a)) stageValue?.SetValue(stage, a);

        if(AutoDestroy) Destroy(this);
    }
    private void Update()
    {
        stage = stageValue.stage;
        value = stageValue.value.ToString();
    }

    private void OnValidate()
    {
        if (HugeInt.TryParse(value, out HugeInt a)) stageValue?.SetValue(stage, a);
    }
}
