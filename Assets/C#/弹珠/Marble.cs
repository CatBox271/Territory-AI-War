using System.Collections;
using TMPro;
using UnityEngine;

public class Marble : MonoBehaviour
{
    static readonly string[] ShortStrings =
    {
        "2", "4", "8", "16", "32", "64", "128", "256", "512", "1K",
        "2K", "4K", "8K", "16K", "32K", "65K", "131K", "262K", "524K", "1M",
        "2M", "4M", "8M", "16M", "33M", "67M", "134M", "268M", "536M", "1B",
        "2B", "4B", "8B", "17B", "34B", "68B", "137B", "274B", "549B", "1T",
        "2T", "4T", "8T", "17T", "35T", "70T", "140T", "281T", "562T", "1P",
    };

    public int stage;
    public float outlineWidth = 0.2f;
    public Material enchantMaterial;
    public AnimationCurve Value2Size;

    public uint ValueExponent => valueExponent;
    private uint valueExponent;
    private uint lastExponent;
    private SpriteRenderer sr;
    private Rigidbody2D rb;
    private IStageValue stageValue;
    private TrailRenderer tr;
    private CircleCollider2D col;
    private TMP_Text tmp;
    private Coroutine trailCoroutine;
    private Material enchantInstance;
    private Vector2 stuckMin, stuckMax;
    private float stuckTime;

    [Header("Stuck Detection")]
    public float stuckRegionRadius = 0.5f;
    public float stuckTimeLimit = 2f;
    public float stuckBounceMinX = -2f;
    public float stuckBounceMaxX = 2f;
    public float stuckBounceMinY = 3f;
    public float stuckBounceMaxY = 6f;

    void Awake()
    {
        sr = GetComponent<SpriteRenderer>();
        rb = GetComponent<Rigidbody2D>();
        stageValue = GetComponent<IStageValue>();
        col = GetComponent<CircleCollider2D>();
        tr = GetComponentInChildren<TrailRenderer>();
        tmp = GetComponentInChildren<TMP_Text>();
    }

    void Start()
    {
        if (sr != null && MapConfig.Instance != null)
            sr.color = MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball);

        if (tr != null)
            ScheduleTrailSetup();

        if (tmp != null)
        {
            UpdateDisplay();
            SetupOutline();
        }

        if (sr != null && enchantMaterial != null)
        {
            enchantInstance = new Material(enchantMaterial);
            sr.material = enchantInstance;
            enchantInstance.SetColor("_EffectColor", MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball));
        }
    }

    void ScheduleTrailSetup()
    {
        if (trailCoroutine != null) StopCoroutine(trailCoroutine);
        trailCoroutine = StartCoroutine(DelayedTrailSetup());
    }

    IEnumerator DelayedTrailSetup()
    {
        yield return new WaitForFixedUpdate();
        SetupTrail();
    }

    public void SetupTrail()
    {
        float r = GetWorldRadius();
        tr.widthMultiplier = r;

        Color col = MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball);
        col += Color.white * 0.15f;
        col.a = 0.75f; tr.startColor = col;
        col.a = 0.25f; tr.endColor = col;
    }

    public void SetupOutline()
    {
        var mat = new Material(tmp.fontSharedMaterial);
        tmp.fontMaterial = mat;
        mat.SetColor("_OutlineColor", MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball));
        mat.SetFloat("_OutlineWidth", outlineWidth);
        mat.EnableKeyword("OUTLINE_ON");
    }

    float GetWorldRadius()
    {
        if (col != null) return col.radius * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y);
        return 0.5f * Mathf.Max(transform.lossyScale.x, transform.lossyScale.y);
    }

    public void SetInitialValue(uint exponent)
    {
        valueExponent = exponent;
        ApplyValue();
    }

    public void MultiplyValue(float times)
    {
        uint add = (uint)Mathf.RoundToInt(Mathf.Log(times, 2));
        SetInitialValue(valueExponent + add);
    }

    void ApplyValue()
    {
        if (stageValue != null)
        {
            stageValue.stage = stage;
            stageValue.value = HugeInt.Pow(2, (int)valueExponent);
        }
        if (rb != null)
        {
            rb.mass = valueExponent + 1;
            if (MarbleManager.Instance != null)
                rb.gravityScale = MarbleManager.Instance.gravity;
        }
        if (tr != null) ScheduleTrailSetup();

        if (tmp != null && valueExponent != lastExponent)
        {
            lastExponent = valueExponent;
            transform.localScale = Vector3.one * Value2Size.Evaluate(valueExponent);
            UpdateDisplay();
        }

        if (enchantInstance != null)
        {
            float opacity = Mathf.Clamp01((valueExponent - 10f) / 30f);
            enchantInstance.SetFloat("_EffectOpacity", opacity);
        }
    }

    void UpdateDisplay()
    {
        int idx = (int)valueExponent - 1;
        if (idx >= 0 && idx < ShortStrings.Length)
            tmp.text = ShortStrings[idx];
        else if (valueExponent == 0)
            tmp.text = "1";
        else
            tmp.text = HugeInt.Pow(2, (int)valueExponent).ToShortString(true);
    }

    public void Home(Shooter shooter)
    {
        transform.position = shooter.transform.position;
        if (rb != null)
        {
            rb.velocity = Vector2.zero;
            shooter.Launch(rb);
        }
        stuckMin = stuckMax = transform.position;
        stuckTime = 0;
    }

    void FixedUpdate()
    {
        if (rb == null) return;
        Vector2 pos = transform.position;

        // expand bounding box
        stuckMin = Vector2.Min(stuckMin, pos);
        stuckMax = Vector2.Max(stuckMax, pos);
        stuckTime += Time.fixedDeltaTime;

        float diagonal = (stuckMax - stuckMin).magnitude;
        if (diagonal > stuckRegionRadius * 2f)
        {
            // moved enough — reset window
            stuckMin = stuckMax = pos;
            stuckTime = 0;
        }
        else if (stuckTime >= stuckTimeLimit)
        {
            rb.velocity = new Vector2(Random.Range(stuckBounceMinX, stuckBounceMaxX), Random.Range(stuckBounceMinY, stuckBounceMaxY));
            stuckMin = stuckMax = pos;
            stuckTime = 0;
        }
    }

    public void Revalue()
    {
        ApplyValue();
    }
}
