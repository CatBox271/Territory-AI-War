using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Text;

public class UIMarbleUpgrade : MonoBehaviour
{
    public static UIMarbleUpgrade Instance;

    private readonly List<SpriteRenderer> sps = new();
    private readonly List<TextMeshPro> texts = new();
    public readonly List<BaseShake> shaked = new();

    private const float UpgradeTextDuration = 2f;
    private readonly Dictionary<int, float> upgradeTextHoldUntil = new();

    [Header("升级连线")]
    public float lineTravelTime = 0.45f;
    public float lineFadeTime = 0.12f;
    public float lineStartWidth = 10f;
    public float lineEndWidth = 4f;
    public Camera lineCamera; // 留空自动选择能看到该点的相机

    private MarbleManager marble;
    private void Awake()
    {
        Instance = this;
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform child = transform.GetChild(i);
            Transform cc = child.GetChild(0);
            sps.Add(child.GetComponent<SpriteRenderer>());
            texts.Add(cc.GetComponent<TextMeshPro>());
            shaked.Add(cc.GetComponent<BaseShake>());
        }
    }
    private void Start()
    {
        marble = MarbleManager.Instance;
        for (int i = 0; i < sps.Count; i++)
        {
            sps[i].color = MapConfig.Instance.GetColor(i + 1, MapConfig.ColorStage.Dark);
            if (texts[i] != null) texts[i].color = MapConfig.Instance.GetText(i + 1);
        }
    }

    public void ShowUpgrade(int stage)
    {
        int index = stage - 1;
        if (index < 0 || index >= sps.Count) return;

        if (index < shaked.Count && shaked[index] != null)
        {
            shaked[index].ToShake(Vector2.zero, 0.5f, 1);
        }

        if (index < texts.Count && texts[index] != null)
        {
            texts[index].text = "升级！";
        }

        upgradeTextHoldUntil[stage] = Time.time + UpgradeTextDuration;
    }

    public Vector3 GetPos(int stage)
    {
        stage--;
        if (stage >= sps.Count || stage < 0) return new();
        return sps[stage].transform.position;
    }

    /// <summary>从升级槽向目标点画线（Canvas UI 线），线头到达后再回调 onArrived。</summary>
    public void PlayLineTo(int stage, Vector3 target, System.Action onArrived)
    {
        StartCoroutine(LineRoutine(stage, target, onArrived));
    }

    private static Sprite lineSprite;
    private static Sprite LineSprite
    {
        get
        {
            if (lineSprite == null)
            {
                Texture2D tex = Texture2D.whiteTexture;
                lineSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return lineSprite;
        }
    }

    private Camera PickCamera(Vector3 worldPos)
    {
        Camera fallback = Camera.main;
        if (fallback == null && Camera.allCameras.Length > 0) fallback = Camera.allCameras[0];

        Camera best = null;
        float bestScore = float.MaxValue;
        foreach (Camera cam in Camera.allCameras)
        {
            if (cam == null || !cam.isActiveAndEnabled) continue;
            Vector3 vp = cam.WorldToViewportPoint(worldPos);
            if (vp.z <= 0f) continue;
            if (vp.x < -0.02f || vp.x > 1.02f || vp.y < -0.02f || vp.y > 1.02f) continue;
            float score = (vp.x - 0.5f) * (vp.x - 0.5f) + (vp.y - 0.5f) * (vp.y - 0.5f);
            if (score < bestScore)
            {
                bestScore = score;
                best = cam;
            }
        }
        return best != null ? best : fallback;
    }

    private Vector2 WorldToCanvasLocal(Vector3 worldPos, Canvas canvas)
    {
        Camera worldCam = lineCamera != null ? lineCamera : PickCamera(worldPos);
        if (worldCam == null) return Vector2.zero;

        Vector2 screen = worldCam.WorldToScreenPoint(worldPos);
        Camera uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)canvas.transform, screen, uiCam, out Vector2 local))
            return local;
        return Vector2.zero;
    }

    private IEnumerator LineRoutine(int stage, Vector3 target, System.Action onArrived)
    {
        int index = stage - 1;
        if (index < 0 || index >= sps.Count)
        {
            onArrived?.Invoke();
            yield break;
        }

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null) canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            onArrived?.Invoke();
            yield break;
        }

        SpriteRenderer slot = sps[index];
        Vector3 fromWorld = slot != null ? slot.transform.position : target;
        Vector2 from = WorldToCanvasLocal(fromWorld, canvas);
        Vector2 to = WorldToCanvasLocal(target, canvas);

        GameObject lineGo = new GameObject("UpgradeLine", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        lineGo.transform.SetParent(canvas.transform, false);
        RectTransform rt = lineGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);

        Image img = lineGo.GetComponent<Image>();
        img.sprite = LineSprite;
        img.raycastTarget = false;

        Color col = MapConfig.Instance != null
            ? MapConfig.Instance.GetColor(stage, MapConfig.ColorStage.Ball)
            : Color.white;
        col = Color.Lerp(col, Color.white, 0.35f);
        col.a = 1f;
        img.color = col;

        float t = 0f;
        while (t < lineTravelTime)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / lineTravelTime);
            k = k * k * (3f - 2f * k);
            Vector2 tip = Vector2.Lerp(from, to, k);
            Vector2 dir = tip - from;
            float dist = dir.magnitude;
            rt.localPosition = (from + tip) * 0.5f;
            rt.sizeDelta = new Vector2(Mathf.Max(dist, lineEndWidth), Mathf.Lerp(lineStartWidth, lineEndWidth, k));
            if (dist > 0.001f)
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
            yield return null;
        }

        Vector2 full = to - from;
        rt.localPosition = (from + to) * 0.5f;
        rt.sizeDelta = new Vector2(Mathf.Max(full.magnitude, lineEndWidth), lineEndWidth);
        if (full.magnitude > 0.001f)
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(full.y, full.x) * Mathf.Rad2Deg);
        onArrived?.Invoke();

        if (lineFadeTime > 0f)
        {
            float f = 0f;
            while (f < lineFadeTime)
            {
                f += Time.deltaTime;
                Color c = img.color;
                c.a = Mathf.Clamp01(1f - f / lineFadeTime);
                img.color = c;
                yield return null;
            }
        }

        Destroy(lineGo);
    }

    /// <summary>在世界坐标位置弹出一个上升并淡出的升级文本。</summary>
    public void PopUpgradeText(Vector3 worldPos, string content, Color color)
    {
        GameObject go = new GameObject("UpgradePopText");
        go.transform.position = worldPos + Vector3.up * 0.7f;

        TextMesh tm = go.AddComponent<TextMesh>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null) tm.font = font;
        tm.text = content;
        tm.fontSize = 72;
        tm.characterSize = 0.04f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.color = color;

        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.sortingOrder = 500;

        StartCoroutine(PopTextRoutine(go, tm));
    }

    private IEnumerator PopTextRoutine(GameObject go, TextMesh tm)
    {
        float duration = 1.2f;
        float t = 0f;
        Vector3 start = go.transform.position;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            go.transform.position = start + Vector3.up * (0.6f * k);
            Color c = tm.color;
            c.a = 1f - k;
            tm.color = c;
            yield return null;
        }
        Destroy(go);
    }
    int pass = 0;
    StringBuilder builder;

    public void Update()
    {
        pass++;
        if (pass <= 10) return;
        pass = 0;

        for (int i = 0; i < sps.Count; i++)
        {
            if (!Towel.AllTowel.TryGetValue(i+1,out Towel t) || t==null || t.isDead)
            {
                sps[i].gameObject.SetActive(false);
                continue;
            }

            if (upgradeTextHoldUntil.TryGetValue(i + 1, out float holdUntil))
            {
                if (Time.time < holdUntil) continue;
                upgradeTextHoldUntil.Remove(i + 1);
            }

            marble.TryGetUpgradeInfo(i + 1, out float progress, out float cost);

            builder = new();
            builder.Append("/");
            builder.Append((int)cost);
            int a = (builder.Length - 1) * 2 + 1;
            builder.Insert(0, (int)progress);
            while (builder.Length < a)
            {
                builder.Insert(0, "_");
            }

            texts[i].text = builder.ToString();
        }
    }
}
