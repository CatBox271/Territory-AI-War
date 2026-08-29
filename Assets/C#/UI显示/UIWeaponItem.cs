using System.Collections;
using UnityEngine;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// 挂在"武器显示"预制体上，负责把武器槽位内容填进名字/数值文本，并播放显示/消失动画。
/// 显示入场：按 show_time 播放 Anim，把曲线值作为 transform.localScale。
/// 消失：反向播放 Anim（缩回），动画结束后 SetActive(false)。
/// 额外只做视觉特效：不修改任何游戏性逻辑。
/// </summary>
public class UIWeaponItem : MonoBehaviour
{
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private TMP_Text valueText;

    public Vector3 v3;//记录初始
    public SpriteRenderer sp;
    public SpriteRenderer pic;

    public AnimationCurve Anim;      // 大小曲线：k(0~1) -> scale
    public float show_time = 1f;     // 动画时长(秒)

    [Header("武器特效（仅视觉）")]
    public Material normalMaterial;   // 普通/锁定态使用的材质（默认 Sprite-Default）
    public Material enchantMaterial;  // 20~29：我的世界附魔特效
    public Material burnMaterial;     // 30+：小丑牌燃烧特效
    public float darkFactor = 0.55f;  // 2^0 ~ 2^13 时颜色变暗的系数

    [Header("特效预览（仅编辑器，勾选 previewEffect 后调 previewValue）")]
    public bool previewEffect;
    [Range(0, 40)] public int previewValue = 20;
    public Color previewTint = Color.white;

    // WeaponDisplayer 场景预览勾选时，运行后强制所有已解锁格子使用同一个预览值。
    public static bool PreviewOverride;
    public static int PreviewOverrideValue = 20;

    private Coroutine coroutine;
    private bool shown;              // 当前是否显示中(用于避免重复播放)
    private MaterialPropertyBlock propertyBlock;
    private Color normalTint = Color.white;
    private bool normalTintSet;

    public enum EffectLevel
    {
        Normal,
        Dark,
        Enchant,
        Burn
    }

    private static readonly HugeInt Pow2_20 = HugeInt.Pow(2, 20);
    private static readonly HugeInt Pow2_30 = HugeInt.Pow(2, 30);

    private static readonly Color EnchantEffectColor = new Color(0.58f, 0.22f, 1f, 1f);
    private const float EnchantOpacity = 0.85f;
    private const float EnchantSpeed = 3f;
    private const float EnchantScale = 2.4f;

    private static readonly Color BurnColor1 = new Color(0.55f, 0.06f, 0.01f, 1f);
    private static readonly Color BurnColor2 = new Color(1f, 0.32f, 0.04f, 1f);
    private static readonly Color BurnColor3 = new Color(1f, 0.85f, 0.4f, 1f);
    private const float BurnIntensity = 1.05f;
    private const float BurnSpeed = 0.85f;
    private const float BurnScale = 6f;
    private const float BurnPower = 2.2f;
    private const float BurnEdgeGlow = 0.55f;

    [System.Serializable]
    public class PIC
    {
        public WeaponKind kind;
        public Sprite pic;
    }
    public List<PIC> pictures;

    private void Awake()
    {
        v3 = transform.localScale;
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        if (sp != null)
        {
            if (normalMaterial == null) normalMaterial = sp.sharedMaterial;
            normalTint = sp.color;
            normalTintSet = true;
        }
    }

    /// <summary>
    /// 判定特效等级。
    /// 游戏实际产生的武器值都是 2^指数，因此：
    ///   2^0~2^13 暗；2^14~2^19 普通；2^20~2^29 附魔；2^30+ 燃烧。
    /// 对小于等于 8192 的非 2 次方预览值，按字面区间兜底：0~19 普通、20~29 附魔、30+ 燃烧，
    /// 方便用 previewValue 直接验证。
    /// </summary>
    public static EffectLevel GetEffectLevel(HugeInt value)
    {
        if (value <= HugeInt.Zero) return EffectLevel.Normal;

        if (value <= (HugeInt)8192)
        {
            if (IsPowerOfTwoSmall(value)) return EffectLevel.Dark;
            if (value < (HugeInt)20) return EffectLevel.Normal;
            if (value < (HugeInt)30) return EffectLevel.Enchant;
            return EffectLevel.Burn;
        }

        if (value < Pow2_20) return EffectLevel.Normal;   // 2^14 ~ 2^19
        if (value < Pow2_30) return EffectLevel.Enchant;  // 2^20 ~ 2^29
        return EffectLevel.Burn;                          // 2^30+
    }

    private static bool IsPowerOfTwoSmall(HugeInt value)
    {
        if (value <= HugeInt.Zero) return false;
        long v = value.ToLong(); // 调用前已保证 value <= 8192
        return v != 0 && (v & (v - 1)) == 0;
    }

    /// <summary>正常显示：名字=武器类型(中文)，数值=短格式，并播放入场。特效只改 sp 视觉。</summary>
    public void SetData(WeaponKind kind, HugeInt val)
    {
        if (nameText != null)
        {
            nameText.text = kind.ToString();
        }
        if (valueText != null) valueText.text = val.ToShortString();
        if (pic != null) pic.sprite = pictures.Find(s => s.kind == kind).pic;

        Color tint = sp != null ? sp.color : Color.white;
        if (sp != null)
        {
            normalTint = tint;
            normalTintSet = true;
        }

        EffectLevel level = PreviewOverride ? GetEffectLevel(PreviewOverrideValue) : GetEffectLevel(val);
        ApplyEffect(level, tint);
        Show();
    }

    /// <summary>锁定状态：名字=锁定，数值=剩余解锁倒计时，并播放入场。锁定态不加特效。</summary>

    /// <summary>设置名字/数值文字颜色（由 WeaponsDisplayer 按阵营传入）。</summary>
    public void SetTextColor(Color color)
    {
        if (nameText != null) nameText.color = color;
        if (valueText != null) valueText.color = color;
    }
    public void SetLocked(string timeText)
    {
        if (nameText != null) nameText.text = "锁定";
        if (valueText != null) valueText.text = timeText;
        ResetEffect();
        Show();
    }

    private void ApplyEffect(EffectLevel level, Color tint)
    {
        if (sp == null) return;

        switch (level)
        {
            case EffectLevel.Normal:
                SetNormal(tint);
                break;

            case EffectLevel.Dark:
                if (normalMaterial != null) sp.sharedMaterial = normalMaterial;
                sp.SetPropertyBlock(null);
                sp.color = tint * darkFactor;
                break;

            case EffectLevel.Enchant:
                ApplyCustomMaterial(enchantMaterial, tint, true);
                break;

            case EffectLevel.Burn:
                ApplyCustomMaterial(burnMaterial, tint, false);
                break;
        }
    }

    private void SetNormal(Color tint)
    {
        if (normalMaterial != null) sp.sharedMaterial = normalMaterial;
        sp.SetPropertyBlock(null);
        sp.color = tint;
    }

    private void ResetEffect()
    {
        if (sp == null) return;
        if (normalMaterial != null) sp.sharedMaterial = normalMaterial;
        sp.SetPropertyBlock(null);
        if (normalTintSet) sp.color = normalTint;
    }

    private void ApplyCustomMaterial(Material effectMaterial, Color tint, bool enchant)
    {
        if (effectMaterial == null)
        {
            SetNormal(tint);
            return;
        }

        sp.sharedMaterial = effectMaterial;
        sp.color = Color.white;

        MaterialPropertyBlock block = GetBlock();
        block.SetColor("_Color", tint);
        if (enchant)
        {
            block.SetColor("_EffectColor", EnchantEffectColor);
            block.SetFloat("_EffectOpacity", EnchantOpacity);
            block.SetFloat("_EffectSpeed", EnchantSpeed);
            block.SetFloat("_EffectScale", EnchantScale);
        }
        else
        {
            block.SetColor("_BurnColor1", BurnColor1);
            block.SetColor("_BurnColor2", BurnColor2);
            block.SetColor("_BurnColor3", BurnColor3);
            block.SetFloat("_BurnIntensity", BurnIntensity);
            block.SetFloat("_BurnSpeed", BurnSpeed);
            block.SetFloat("_BurnScale", BurnScale);
            block.SetFloat("_BurnPower", BurnPower);
            block.SetFloat("_BurnEdgeGlow", BurnEdgeGlow);
        }
        sp.SetPropertyBlock(block);
    }

    private MaterialPropertyBlock GetBlock()
    {
        if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();
        return propertyBlock;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // 勾选 previewEffect 即可在预制体/场景里直接看效果，不会改游戏逻辑。
        if (sp == null) sp = GetComponent<SpriteRenderer>();
        if (sp == null) return;
        if (normalMaterial == null) normalMaterial = sp.sharedMaterial;

        if (previewEffect)
        {
            ApplyPreviewEffect();
        }
        else
        {
            if (normalMaterial != null) sp.sharedMaterial = normalMaterial;
            sp.SetPropertyBlock(null);
        }
    }

    private void ApplyPreviewEffect()
    {
        EffectLevel level = GetEffectLevel(previewValue);

        Material mat = normalMaterial;
        if (level == EffectLevel.Enchant) mat = enchantMaterial;
        else if (level == EffectLevel.Burn) mat = burnMaterial;

        if (mat != null) sp.sharedMaterial = mat;
        sp.SetPropertyBlock(null);

        if (level == EffectLevel.Normal) return;

        MaterialPropertyBlock block = GetBlock();
        if (level == EffectLevel.Dark)
        {
            block.SetColor("_Color", previewTint * darkFactor);
        }
        else if (level == EffectLevel.Enchant)
        {
            block.SetColor("_Color", previewTint);
            block.SetColor("_EffectColor", EnchantEffectColor);
            block.SetFloat("_EffectOpacity", EnchantOpacity);
            block.SetFloat("_EffectSpeed", EnchantSpeed);
            block.SetFloat("_EffectScale", EnchantScale);
        }
        else
        {
            block.SetColor("_Color", previewTint);
            block.SetColor("_BurnColor1", BurnColor1);
            block.SetColor("_BurnColor2", BurnColor2);
            block.SetColor("_BurnColor3", BurnColor3);
            block.SetFloat("_BurnIntensity", BurnIntensity);
            block.SetFloat("_BurnSpeed", BurnSpeed);
            block.SetFloat("_BurnScale", BurnScale);
            block.SetFloat("_BurnPower", BurnPower);
            block.SetFloat("_BurnEdgeGlow", BurnEdgeGlow);
        }
        sp.SetPropertyBlock(block);
    }
#endif

    // 显示入场：仅在"隐藏 -> 显示"切换时播放一次
    public void Show()
    {
        if (shown) return;
        shown = true;
        gameObject.SetActive(true);
        StopAnim();
        coroutine = StartCoroutine(PlayScale(false));
    }

    // 消失：反向播放 Anim，结束后隐藏
    public void Hide()
    {
        if (!shown) return;
        shown = false;
        StopAnim();
        coroutine = StartCoroutine(PlayScale(true));
    }

    private void StopAnim()
    {
        if (coroutine != null) { StopCoroutine(coroutine); coroutine = null; }
    }

    private IEnumerator PlayScale(bool reverse)
    {
        float t = 0;
        while (t < show_time)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(show_time, 0.0001f));
            float v = Anim != null ? Anim.Evaluate(reverse ? 1f - k : k) : (reverse ? 1f - k : k);
            transform.localScale = v3 * Mathf.Max(0f, v);
            yield return null;
        }

        float final = Anim != null ? Anim.Evaluate(reverse ? 0f : 1f) : (reverse ? 0f : 1f);
        transform.localScale = v3 * Mathf.Max(0f, final);
        coroutine = null;

        if (reverse) gameObject.SetActive(false);
    }

    private void OnDisable()
    {
        StopAnim();
    }
}
