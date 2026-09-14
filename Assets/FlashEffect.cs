using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FlashEffect : MonoBehaviour
{
    public SpriteRenderer sp;
    private Color originColor;

    [Range(0, 1)] public float intensityPerHit = 0.4f;
    public float decayDuration = 3f;
    private float flashIntensity;
    private Coroutine flashCoroutine;

    private void Awake()
    {
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        if (sp != null) originColor = sp.color;
    }

    public void WhileBeHit()
    {
        flashIntensity = Mathf.Min(1, flashIntensity + intensityPerHit);
        if (flashCoroutine == null)
            flashCoroutine = StartCoroutine(FlashWhite());
    }
    public void WhileBeHit(float intensity)
    {
        flashIntensity = Mathf.Min(1, flashIntensity + intensity);
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
