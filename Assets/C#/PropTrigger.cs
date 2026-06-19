using TMPro;
using UnityEngine;

public class PropTrigger : MonoBehaviour
{
    public string itemName;
    public TextMeshPro text;

    void OnValidate()
    {
        if (text != null)
            text.text = itemName;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out Marble marble)) return;

        HugeInt val = HugeInt.Pow(2, (int)marble.ValueExponent);
        var cfg = MapConfig.Instance;
        cfg.AddProp(marble.stage, itemName, val);

        if (MarbleManager.Instance != null)
            marble.SetInitialValue(MarbleManager.Instance.initialValueExponent);

        if (MarbleManager.Instance != null && MarbleManager.Instance.Shooter != null)
        {
            var shooter = MarbleManager.Instance.Shooter.GetComponent<Shooter>();
            if (shooter != null)
                marble.Home(shooter);
        }

        if (!cfg.useAIDecision)
            cfg.ExecutePropEffect(marble.stage, itemName, val);
    }
}
