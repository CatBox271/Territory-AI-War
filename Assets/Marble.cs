using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class Marble : MonoBehaviour
{
    public float Times;
    public TextMeshPro text;
    private void OnValidate()
    {
        text.text = "¡Á" + Times;
    }
}
