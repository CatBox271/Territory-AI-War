using UnityEngine;

public class FlashEffect : MonoBehaviour
{
    public SpriteRenderer sp;
    public Material orign_material;
    public Material effect_material;


    private Material instance;
    private Material effectSource;

    void Awake()
    {
        if (sp == null) TryGetComponent(out sp);
        if (sp != null && orign_material == null) orign_material = sp.sharedMaterial;
    }

    public Material GetEffect()
    {
        return sp != null ? sp.sharedMaterial : null;
    }

    public void SetEffect(bool on)
    {
        if (on) ApplyEffect(); else RemoveEffect();
    }

    void ApplyEffect()
    {
        if (sp == null) TryGetComponent(out sp);
        if (sp == null) return;

        // 原始材质还没记录时补一次
        if (orign_material == null) orign_material = sp.sharedMaterial;

        // 未配置闪光材质时，回到原始材质并清理
        if (effect_material == null)
        {
            RemoveEffect();
            return;
        }

        // 同一个效果材质且仍在生效：复用实例，不重复 new
        if (instance != null && effectSource == effect_material && GetEffect() == instance) return;

        DestroyInstance();

        effectSource = effect_material;
        instance = new Material(effect_material);
        instance.name = effect_material.name + " (FlashEffect)";
        sp.sharedMaterial = instance;
    }

    void RemoveEffect()
    {
        if (sp == null) return;

        // 只有当前材质确实是我们创建的实例时才还原，避免覆盖其他脚本刚换上的材质
        if (instance != null && orign_material != null && GetEffect() == instance) sp.sharedMaterial = orign_material;

        DestroyInstance();
    }

    void DestroyInstance()
    {
        if (instance == null) return;

        if (Application.isPlaying) Destroy(instance);
        else DestroyImmediate(instance);

        instance = null;
        effectSource = null;
    }

    void OnDestroy()
    {
        DestroyInstance();
    }
}