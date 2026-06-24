using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WidthEditor : MonoBehaviour
{
    public bool Refresh;

    private void OnValidate()
    {
        transform.GetChild(0).localScale = new Vector3(1 / transform.lossyScale.x, 1 / transform.lossyScale.y, 1 / transform.lossyScale.z);
    }
}
