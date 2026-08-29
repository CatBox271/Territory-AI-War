using TMPro;
using UnityEngine;

public class PropTrigger : MonoBehaviour
{
    public WeaponKind itemName;
    public TextMeshPro text;

    void OnValidate()
    {
        if (text != null)
            text.text = itemName.ToString();
    }

    MapConfig cfg;
    private void Start()
    {
        cfg = MapConfig.Instance;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out Marble marble)) return;

        HugeInt val = HugeInt.Pow(2, (int)marble.ValueExponent);

        cfg.AddProp(marble.stage, itemName, val);

        if (MarbleManager.Instance != null)
        {
            marble.SetInitialValue(MarbleManager.Instance.initialValueExponent);
            var shooter = MarbleManager.Instance.GetRandomShooter();
            if (shooter != null)
                marble.Home(shooter);
        }
    }
}
