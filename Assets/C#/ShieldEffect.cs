using System.Collections;
using UnityEngine;

public class ShieldEffect : MonoBehaviour, IStageValue
{
    public int stage { get; set; }
    public HugeInt value { get; set; }

    public  SpriteRenderer sp;
    public Color originColor;

    [Range(0, 1)] public float intensityPerHit = 0.4f;
    public float decayDuration = 0.3f;
    private float flashIntensity;
    private Coroutine flashCoroutine;

    private void Awake()
    {
        if (sp == null)
        {
            sp = GetComponent<SpriteRenderer>();
            originColor = sp.color;
        }
    }

    public void WhileBeHit(int _stage, HugeInt _value)
    {
        flashIntensity = Mathf.Min(1, flashIntensity + intensityPerHit);
        if (flashCoroutine == null)
            flashCoroutine = StartCoroutine(FlashWhite());
    }

    private IEnumerator FlashWhite()
    {
        while (flashIntensity > 0.001f)
        {
            flashIntensity = Mathf.Max(0, flashIntensity - Time.deltaTime / decayDuration);
            sp.color = Color.Lerp(originColor, Color.white, flashIntensity);
            yield return null;
        }
        flashIntensity = 0;
        sp.color = originColor;
        flashCoroutine = null;
    }
}
