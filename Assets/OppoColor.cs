using TMPro;
using UnityEngine;

/// <summary>
/// 自动把挂载字体（TMP_Text）的描边颜色设为内部填充色的反色。
/// 复制当前字体材质到实例再修改，避免影响全局共享材质。
/// </summary>
public class OppoColor : MonoBehaviour
{
    [Tooltip("要控制的字体显示；留空则自动取本物体上的 TMP_Text")]
    public TMP_Text text;

    private Material instance;
    private static readonly int OutlineColor = Shader.PropertyToID("_OutlineColor");

    void Awake()
    {
        if (text == null) text = GetComponent<TMP_Text>();
        CopyMaterial();
    }

    void LateUpdate()
    {
        Sync();
    }

    void CopyMaterial()
    {
        if (text == null || text.fontSharedMaterial == null) return;

        instance = new Material(text.fontSharedMaterial);
        instance.name = text.fontSharedMaterial.name + " (OppoColor)";
        text.fontMaterial = instance;
    }

    void Sync()
    {
        if (text == null) text = GetComponent<TMP_Text>();
        if (text == null) return;

        Material current = text.fontMaterial;
        if (current == null) return;

        // 材质被其他脚本换掉时，基于当前材质重新复制，避免误改共享材质
        if (instance == null || current != instance)
        {
            if (text.fontSharedMaterial == null) return;

            instance = new Material(text.fontSharedMaterial);
            instance.name += " (OppoColor)";
            text.fontMaterial = instance;
            current = instance;
        }

        Color inner = text.color;
        Color inverse = new Color(1f - inner.r, 1f - inner.g, 1f - inner.b, inner.a);
        if (current.HasProperty(OutlineColor))
            current.SetColor(OutlineColor, inverse);
    }
}