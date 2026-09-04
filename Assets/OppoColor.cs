using TMPro;
using UnityEngine;

/// <summary>
/// 自动把挂载字体（TMP_Text）的描边颜色设为内部填充色的反色。
/// 复制当前字体材质到实例再修改，避免影响全局共享材质。
/// 注意：TMP 的 fontMaterial getter 每次访问都可能创建新的材质实例，
/// 因此每帧检查时必须读 fontSharedMaterial，绝不能读 fontMaterial。
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
        EnsureInstance();
    }

    void LateUpdate()
    {
        Sync();
    }

    void EnsureInstance()
    {
        if (text == null || text.fontSharedMaterial == null) return;

        // 已经是自己的材质实例时直接复用，不重复创建
        if (instance != null && text.fontSharedMaterial == instance) return;

        instance = CreateMaterialInstance(text.fontSharedMaterial);
        text.fontMaterial = instance;
    }

    void Sync()
    {
        if (text == null) text = GetComponent<TMP_Text>();
        if (text == null) return;

        // 关键：这里读 fontSharedMaterial。
        // fontMaterial 的 getter 在 TMP 中会调用 GetMaterial()，
        // 材质实例不一致时每调用一次就 new 一个 Material，
        // 放在 LateUpdate 里会导致每帧创建材质，内存持续增长。
        Material current = text.fontSharedMaterial;
        if (current == null) return;

        // 材质被其他脚本换掉时，基于当前材质重新复制，避免误改共享材质
        if (instance == null || current != instance)
        {
            DestroyMaterial();
            instance = CreateMaterialInstance(current);
            text.fontMaterial = instance;
            current = instance;
        }

        Color inner = text.color;
        Color inverse = new Color(1f - inner.r, 1f - inner.g, 1f - inner.b, inner.a);
        if (current.HasProperty(OutlineColor))
            current.SetColor(OutlineColor, inverse);
    }

    Material CreateMaterialInstance(Material source)
    {
        Material material = new Material(source);

        string baseName = source == null ? "TMP Material" : source.name;
        const string suffix = " (OppoColor)";
        if (baseName.EndsWith(suffix))
            baseName = baseName.Substring(0, baseName.Length - suffix.Length);

        material.name = baseName + suffix;
        return material;
    }

    void DestroyMaterial()
    {
        if (instance == null) return;

        if (Application.isPlaying) Destroy(instance);
        else DestroyImmediate(instance);
        instance = null;
    }

    void OnDestroy()
    {
        DestroyMaterial();
    }
}