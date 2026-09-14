using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class StoryTeller : MonoBehaviour
{
    [Header("相机设置")]
    public Camera camera;
    [Header("背景参数设置")]
    public SpriteRenderer BackGroundSprite;
    public float BackGroundDuration = 0.5f;
    public float BackGroundBlur = 12f;
    public float BackGroundTransparent = 0.75f;
    public AnimationCurve BackGroundCurve = AnimationCurve.Linear(0, 0, 1, 1);
    public bool test;
    private void OnValidate()
    {
        if (test)
        {
            OpenScene();
        }
        else
        {
            CloseScene();
        }
    }
    public void OpenScene(bool immediately = false)
    {
        camera.enabled = true;
        ClearScene(true);
        StartCoroutine(EBackGroundSwitch(BackGroundDuration, true));
    }
    public void CloseScene(bool immediately = false)
    {
        StartCoroutine(ECloseScene(immediately));
    }
    IEnumerator ECloseScene(bool immediately = false)
    {
        ClearScene(immediately);
        yield return EBackGroundSwitch(BackGroundDuration, false);
        camera.enabled = false;
    }
    public void ClearScene(bool immediately = false)
    { 
        
    }

    IEnumerator EBackGroundSwitch(float time ,bool on)
    {
        float BlurEnd = on ? BackGroundBlur : 0;
        float AlphaEnd = on ? BackGroundTransparent : 0;
        Color col = BackGroundSprite.color;
        if (time <= 0)
        {
            BackGroundSprite.material.SetFloat("_Size", BlurEnd);
            col.a = AlphaEnd;
            BackGroundSprite.color = col;
        }
        else
        {
            float BlurStart = BackGroundSprite.material.GetFloat("_Size");
            float AlphaStart = col.a;
            float t = 0;
            time = Mathf.Max(0.1f, time);
            while (true)
            {
                float curveT = BackGroundCurve.Evaluate(t);
                BackGroundSprite.material.SetFloat("_Size", Mathf.Lerp(BlurStart, BlurEnd, curveT));
                col.a = Mathf.Lerp(AlphaStart, AlphaEnd, curveT);
                BackGroundSprite.color = col;
                if (t >= 1) break;
                yield return null;
                t += Time.deltaTime / time;
            }
        }
    }
}
