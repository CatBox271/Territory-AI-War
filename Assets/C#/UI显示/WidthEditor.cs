using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WidthEditor : MonoBehaviour
{
    public bool Refresh;
    public float times = 1;
    public Transform tf;

    private void OnValidate()
    {
        if (tf == null) if (transform.childCount != 0) tf = transform.GetChild(0);
        if (tf == null) return;
        tf.localScale = new Vector3(
            1 / transform.lossyScale.x,
            1 / transform.lossyScale.y,
            1 / transform.lossyScale.z
        ) * times;
    }
}
