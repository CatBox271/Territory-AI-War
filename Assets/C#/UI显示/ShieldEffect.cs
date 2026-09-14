using System.Collections;
using UnityEngine;

public class ShieldEffect : MonoBehaviour, IStageValue
{
    public int stage { get; set; }
    public int hurtSourceStage { get; set; }
    public string hurtSourceGuid { get; set; }
    public string hurtSourceDesc { get; set; }
    public HugeInt value { get; set; }

    public FlashEffect flash;

    private void Awake()
    {
        TryGetComponent(out flash);
    }
    public void WhileBeHit(int _stage, HugeInt _value)
    {
        flash?.WhileBeHit();
    }
}