using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class ShieldShow : MonoBehaviour
{
    public Towel towel;
    public Text text;
    bool up = true;
    HugeInt last = -1;
    void Update()
    {
        if (text == null || towel == null) return;
        if (last == towel.shield_value) return;
        last = towel.shield_value;
        text.text = towel.shield_value.ToShortString();
        if (transform.position.y > 0)
        {
            if (up)
            {
                up = false;
                Vector3 v3 = transform.localPosition;
                v3.y = Mathf.Abs(v3.y) * -1;
                transform.localPosition = v3;
            }
        }
        else
        {
            if (!up)
            {
                up = true;
                Vector3 v3 = transform.localPosition;
                v3.y = Mathf.Abs(v3.y);
                transform.localPosition = v3;
            }
        }
    }
}
