using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class UIMessageBar : MonoBehaviour
{
    public SpriteRenderer background;
    public TextMeshPro text;
    public FlashEffect effect;

    public bool focus = false;

    public Vector2 YRange= new();
    public Vector2 BackgroundAlpha = new(0.5f, 0.5f);
    public Vector2 TextAlpha = new(1f, 0.5f);
    public float FadeMin = 0.5f;
    public float FadeMax = 1f;
    private float FadeRange { get { return FadeMax - FadeMin; } }

    public void Set(string content,Color col)
    {
        text.text = content;
        background.color = col;
        Refresh();
    }

    public void Refresh()
    {
        if (focus) LayerUp(); else LayerDown();
    }

    private void LayerUp()
    {
        Color col;

        background.sortingOrder = -4;
        col = background.color;
        col.a = BackgroundAlpha[0];
        background.color= col;

        text.sortingOrder = -3;
        col = text.color;
        col.a = TextAlpha[0];
        text.color = col;

        effect.SetEffect(true);
    }

    private void LayerDown()
    {
        float range = (1 - (transform.localPosition.y - YRange[0]) / (YRange[1] - YRange[0])) * FadeRange + FadeMin;
        Color col;

        background.sortingOrder = -11;
        col = background.color;
        col.a = BackgroundAlpha[1] * range;
        background.color = col;

        text.sortingOrder = -10;
        col = text.color;
        col.a = TextAlpha[1] * range;
        text.color = col;

        effect.SetEffect(false);
    }
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (focus) LayerUp(); else LayerDown();
    }
}
