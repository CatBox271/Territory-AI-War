using TMPro;
using UnityEngine;

public class MarbleTrigger : MonoBehaviour
{
    public float Times;
    public TextMeshPro text;

    void OnValidate()
    {
        if (text != null)
            text.text = "×" + Times;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out Marble marble)) return;

        marble.MultiplyValue(Times);

        if (MarbleManager.Instance != null)
        {
            var shooter = MarbleManager.Instance.GetRandomShooter();
            if (shooter != null)
                marble.Home(shooter);
        }
    }
}
