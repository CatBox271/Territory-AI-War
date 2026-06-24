using System.Collections;
using UnityEngine;

[RequireComponent(typeof(SpriteRenderer))]
public class CrashFlash : MonoBehaviour
{
    public Color addition = Color.white;
    public float duration = 0.1f;

    private SpriteRenderer sr;
    private Color originColor;
    private Coroutine flashRoutine;

    void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
        originColor = sr.color;
    }

    void OnCollisionExit2D(Collision2D collision)
    {
        if (flashRoutine != null)
            StopCoroutine(flashRoutine);
        flashRoutine = StartCoroutine(Flash());
    }

    IEnumerator Flash()
    {
        sr.color = originColor + addition;
        yield return new WaitForSeconds(duration);
        sr.color = originColor;
    }
}
