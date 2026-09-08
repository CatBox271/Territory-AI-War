using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public enum SpriteEmotion
{
    origin,
    smile,
    laugh,
    shock,
    angry,
    sad,
    win,
    fail,
}

public class UISprite : MonoBehaviour
{
    public SpriteRenderer sp;
    public enum XR
    { 
        None,
        Left,
        Right,
    }
    public enum YR
    {
        None,
        Top,
        Botton,
    }
    public Animation anim;

    public XR x_relative;
    public YR y_relative;

    public float x_relative_value;
    public float y_relative_value;

    public float Scale = 1.7f;
    public float additive_scale = 0f;
    public Vector3 additive_pos = new();
    private Vector3 last_additive_pos = new();

    private Vector3 aim_pos;
    private Vector3 delta_pos;
    private void OnValidate()
    {
        Adjust();
    }

    public void Adjust()
    {
        aim_pos = new(
            x_relative switch{
                XR.Left => -(x_relative_value + additive_pos.x),
                XR.Right => (x_relative_value + additive_pos.x),
                _ => 0,
            },
            y_relative switch{
                YR.Top => (y_relative_value + additive_pos.y),
                YR.Botton => -(y_relative_value + additive_pos.y),
                _ => 0,
            });
        transform.localPosition = delta_pos + aim_pos;
        transform.localScale = Vector3.one * (Scale + additive_scale);
    }
    public bool show;
    public void Set(int stage, SpriteEmotion emo)
    {
        try
        {
            //我在写别动
            sp.sprite = Resources.Load(AIAgent.GetStageName(stage) + "_" + emo.ToString()) as Sprite;
            if (AIAgent.Instance != null)
            {
                var card = AIAgent.Instance.cards.Find(c => c.position == stage);
                if (card != null)
                {
                    if (card.x_relative != XR.None)
                    {
                        x_relative = card.x_relative;
                        x_relative_value = card.RelativePos.x;
                    }
                    if (card.y_relative != YR.None)
                    {
                        y_relative = card.y_relative;
                        y_relative_value = card.RelativePos.y;
                    }
                    if (Mathf.Abs(card.Scale - 1.7f) > 0.0001f)
                        Scale = card.Scale;
                    Adjust();
                }
            }
        }
        catch
        { }
    }
    public void Act(bool show)
    {
        if (show) anim.Play("SpriteDuration");
        else anim.Play("SpriteFade");
        this.show = show;
    }
    private void Update()
    {
        if (last_additive_pos != additive_pos)
        {
            Adjust();
            last_additive_pos = additive_pos;
        }
    }


}
