using UnityEngine;
using TMPro;

public class FieldArrivalModifier : MonoBehaviour
{
    enum State { Idle, Entering, Falling, Done }

    // ====== 时间 ======
    [Header("时间（每个字符独立计时）")]
    [Tooltip("每个字符动画的时长（秒）")]
    public float charDuration = 0.4f;
    [Tooltip("相邻两个字符启动的时间间隔（秒）")]
    public float charInterval = 0.06f;

    // ====== 位置 ======
    [Header("位置")]
    [Tooltip("位置移动的缓动曲线")]
    public AnimationCurve positionCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    // ====== 缩放 ======
    [Header("缩放（每个字符独立）")]
    [Tooltip("缩放曲线：x=本字符进度(0→1)，y=缩放倍率。建议 0→1.2→1（overshoot）")]
    public AnimationCurve scaleCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    // ====== 旋转 ======
    [Header("旋转（每个字符独立）")]
    [Tooltip("每个字符的随机初始旋转角上限（度）")]
    [Range(0, 360)] public float randomRotationMax = 60f;


    // ====== 颜色 ======
    [Header("颜色")]
    [Tooltip("这条文本的透明度：Play 时把它的 a 写进 TMP 的 alpha（不再硬写 1）。" +
             "RGB 不写 —— 阵营色 / 舞台配色是外面写进 TMP 的")]
    public Color color = Color.white;

    [Header("是否消失")]
    public bool fadable = true;
    // ====== 消失 ======
    [Header("消失（所有字符同时淡出）")]
    [Tooltip("淡出时长（秒）")]
    public float fallFadeTime = 0.5f;

    // ====== 生命周期 ======
    [Header("生命周期")]
    [Tooltip("每个字符贡献的显示时间（秒），总显示时间 = 字符数 × 此值")]
    public float charDisplayTime = 0.15f;
    [Tooltip("显示时间下限（秒），取 max(字符数×每字时间, 此值)")]
    public float minDisplayTime = 1.5f;

    // ====== 通用 ======
    [Header("通用")]
    [Tooltip("全局动画速度倍率")]
    public float speedMultiplier = 1f;
    [Tooltip("用不受 timeScale 影响的真实时间推进（useStageClock 关闭时才生效）")]
    public bool useUnscaledTime = false;
    [Tooltip("用**舞台时钟**推进：一个游戏帧一 tick，一 tick = 1/CaptureFrame 秒。" +
             "舞台上的 Text 用这个（和 WaitForCaptureUpdate 同一个钟），速度和演出其它部分一致、录像里确定；" +
             "战场上的飘字保持关闭")]
    public bool useStageClock = false;

    private int _stageFrame = int.MinValue;
    private int _stageWait;
    [Tooltip("激活时自动播放")]
    public bool playOnEnable = true;

    private TextMeshPro _text;
    private Vector3[][] _originalVertices;
    private Color32[][] _originalColors;
    private int[][] _vertexToChar;
    private Vector2[] _charCenters;
    private float[] _charRandomAngle;

    private Vector3 _worldStartPoint;
    private Vector3 _localStartPos;
    private float _timer;
    private State _state;
    private int _visibleCharCount;
    private bool _hasStartPoint;

    public bool IsPlaying => _state == State.Entering || _state == State.Falling;

    public float TotalDuration => _visibleCharCount > 0
        ? charDuration + (_visibleCharCount - 1) * charInterval
        : charDuration;

    public float TotalFallDuration => fallFadeTime;
    public float TotalLifeTime => TotalDuration + Mathf.Max(_visibleCharCount * charDisplayTime, minDisplayTime);

    void Awake()
    {
        _text = GetComponent<TextMeshPro>();
    }

    void OnEnable()
    {
        if (playOnEnable && _state == State.Idle) Play();
    }

    void OnDisable()
    {
        _state = State.Idle;
    }

    public void SetStartPoint(Vector3 worldPos)
    {
        _worldStartPoint = worldPos;
        _hasStartPoint = true;
    }

    public void Play()
    {
        if (_text == null) return;
        _state = State.Entering;
        _timer = 0f;
        _stageFrame = int.MinValue;   // 舞台时钟：下一帧只记基准，不把中间停的时间补进来
        ApplyColorAlpha();
        CacheTargetVertices();
    }

    public void PlayWithText(string newText)
    {
        if (_text == null) return;
        _text.text = newText;
        Play();
    }

    /// <summary>
    /// 立刻按 t=0 把入场状态算一遍（不推进时间）。
    /// Play / PlayWithText 之后必须调一次：不然这一段字会先以「最终位置 + 全亮」渲染一帧，
    /// 下一帧 Update 才把它们拉回起点 —— 看起来就是「先出现到最终位置再弹动画」。
    /// </summary>
    public void ApplyEnterNow()
    {
        if (_state != State.Entering || _text == null || _originalVertices == null) return;
        ApplyEnterModification();
    }

    public void PlayFallOut()
    {
        if (!fadable) return;
        if (_text == null || _visibleCharCount <= 0) return;

        _state = State.Falling;
        _timer = 0f;

        _text.ForceMeshUpdate();
        CacheOriginalVertices();
    }

    public void Stop()
    {
        _state = State.Idle;
    }

    /// <summary>
    /// 把 color 的 a 写进文本的透明度。**RGB 一个字节都不动**：阵营色（CharacterStatusArea / MessageDisplayer）
    /// 和舞台配色（SceneTextDisplay）是外面写进 TMP 的，这里只接管「这条字有多透」。
    /// </summary>
    private void ApplyColorAlpha()
    {
        if (_text == null) return;
        Color c = _text.color;
        c.a = color.a;
        _text.color = c;
    }

    // ====== 缓存 ======

    void CacheTargetVertices()
    {
        _text.ForceMeshUpdate();
        TMP_TextInfo textInfo = _text.textInfo;
        int meshCount = textInfo.meshInfo.Length;

        _originalVertices = new Vector3[meshCount][];
        _originalColors = new Color32[meshCount][];
        _vertexToChar = new int[meshCount][];

        _visibleCharCount = 0;
        for (int c = 0; c < textInfo.characterCount; c++)
            if (textInfo.characterInfo[c].isVisible)
                _visibleCharCount++;

        _charCenters = new Vector2[_visibleCharCount];
        _charRandomAngle = new float[_visibleCharCount];
        for (int c = 0; c < _visibleCharCount; c++)
            _charRandomAngle[c] = Random.Range(-randomRotationMax, randomRotationMax) * Mathf.Deg2Rad;

        _localStartPos = _hasStartPoint
            ? _text.transform.InverseTransformPoint(_worldStartPoint)
            : _text.transform.InverseTransformPoint(_text.transform.position);

        int[] charOrder = new int[_visibleCharCount];
        int idx = 0;
        for (int c = 0; c < textInfo.characterCount; c++)
            if (textInfo.characterInfo[c].isVisible)
                charOrder[idx++] = c;

        for (int m = 0; m < meshCount; m++)
        {
            var meshInfo = textInfo.meshInfo[m];
            int vCount = meshInfo.vertices.Length;
            _originalVertices[m] = new Vector3[vCount];
            _originalColors[m] = new Color32[vCount];
            _vertexToChar[m] = new int[vCount];

            System.Array.Copy(meshInfo.vertices, _originalVertices[m], vCount);
            if (meshInfo.mesh.colors32 != null && meshInfo.mesh.colors32.Length == vCount)
                System.Array.Copy(meshInfo.mesh.colors32, _originalColors[m], vCount);
            else
                for (int v = 0; v < vCount; v++)
                    _originalColors[m][v] = new Color32(255, 255, 255, 255);

            for (int v = 0; v < vCount; v++)
                _vertexToChar[m][v] = -1;

            for (int ci = 0; ci < charOrder.Length; ci++)
            {
                int c = charOrder[ci];
                var charInfo = textInfo.characterInfo[c];
                int vi = charInfo.vertexIndex;
                int matIdx = charInfo.materialReferenceIndex;
                if (matIdx != m) continue;

                for (int v = 0; v < 4 && vi + v < vCount; v++)
                    _vertexToChar[m][vi + v] = ci;

                if (vi + 3 < vCount)
                {
                    Vector3 v0 = _originalVertices[m][vi];
                    Vector3 v2 = _originalVertices[m][vi + 2];
                    _charCenters[ci] = (v0 + v2) * 0.5f;
                }
            }
        }
    }

    void CacheOriginalVertices()
    {
        TMP_TextInfo textInfo = _text.textInfo;
        int meshCount = textInfo.meshInfo.Length;
        _originalVertices = new Vector3[meshCount][];
        _originalColors = new Color32[meshCount][];
        _vertexToChar = new int[meshCount][];

        int[] charOrder = new int[_visibleCharCount];
        int idx = 0;
        for (int c = 0; c < textInfo.characterCount; c++)
            if (textInfo.characterInfo[c].isVisible)
                charOrder[idx++] = c;

        for (int m = 0; m < meshCount; m++)
        {
            var meshInfo = textInfo.meshInfo[m];
            int vCount = meshInfo.vertices.Length;
            _originalVertices[m] = new Vector3[vCount];
            _originalColors[m] = new Color32[vCount];
            _vertexToChar[m] = new int[vCount];

            System.Array.Copy(meshInfo.vertices, _originalVertices[m], vCount);
            if (meshInfo.mesh.colors32 != null && meshInfo.mesh.colors32.Length == vCount)
                System.Array.Copy(meshInfo.mesh.colors32, _originalColors[m], vCount);
            else
                for (int v = 0; v < vCount; v++)
                    _originalColors[m][v] = new Color32(255, 255, 255, 255);

            for (int v = 0; v < vCount; v++)
                _vertexToChar[m][v] = -1;

            for (int ci = 0; ci < charOrder.Length; ci++)
            {
                int c = charOrder[ci];
                var charInfo = textInfo.characterInfo[c];
                int vi = charInfo.vertexIndex;
                int matIdx = charInfo.materialReferenceIndex;
                if (matIdx != m) continue;

                for (int v = 0; v < 4 && vi + v < vCount; v++)
                    _vertexToChar[m][vi + v] = ci;
            }
        }
    }

    // ====== Update ======

    void Update()
    {
        if (_text == null || _originalVertices == null) return;

        float dt;
        if (useStageClock)
        {
            dt = StageClockDelta();
            if (dt <= 0f)
            {
                // 舞台时钟这一帧没动（没 tick / 没有 StoryTeller）：连续几帧不动就退回真实时间，
                // 免得整段字一直卡在「还没入场」的不可见状态出不来。
                _stageWait++;
                if (_stageWait < 5) return;
                dt = Time.unscaledDeltaTime * speedMultiplier;
            }
            else
            {
                _stageWait = 0;
            }
        }
        else
        {
            dt = (useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime) * speedMultiplier;
        }

        switch (_state)
        {
            case State.Entering: UpdateEnter(dt); break;
            case State.Idle:     UpdateIdle(dt);  break;
            case State.Falling:  UpdateFall(dt);  break;
        }
    }

    /// <summary>
    /// 这一帧舞台推进了多少秒。只**读**舞台时钟的帧计数（StoryTeller.ClockFrame），不订阅、不改它的逻辑：
    /// 舞台一帧 = 1/CaptureFrame 秒，游戏跑多少帧它就推进多少，所以录像里的速度是确定的。
    /// 舞台不在（编辑器里单独看 prefab）时返回 0，动画就停着。
    /// </summary>
    private float StageClockDelta()
    {
        StoryTeller stage = StoryTeller.Instance;
        if (stage == null) return 0f;

        int f = stage.ClockFrame;
        if (_stageFrame == int.MinValue)   // 刚播：只记基准，不补这一帧
        {
            _stageFrame = f;
            return 0f;
        }
        if (f == _stageFrame) return 0f;   // 舞台这一帧没动

        int ticks = f - _stageFrame;
        _stageFrame = f;
        return ticks * stage.ClockDelta * speedMultiplier;
    }

    void UpdateIdle(float dt)
    {
        _timer += dt;
        if (_timer >= TotalLifeTime)
            PlayFallOut();
    }

    void UpdateEnter(float dt)
    {
        _timer += dt;
        ApplyEnterModification();

        if (_timer >= TotalDuration)
        {
            _state = State.Idle;
            ApplyEnterModification(forceComplete: true);
        }
    }

    void ApplyEnterModification(bool forceComplete = false)
    {
        TMP_TextInfo textInfo = _text.textInfo;

        for (int m = 0; m < textInfo.meshInfo.Length; m++)
        {
            var meshInfo = textInfo.meshInfo[m];
            if (meshInfo.vertices == null || _originalVertices[m] == null) continue;

            Vector3[] verts = meshInfo.vertices;
            Color32[] colors = meshInfo.mesh.colors32;
            int vCount = verts.Length;

            for (int v = 0; v < vCount; v++)
            {
                int charIdx = _vertexToChar[m][v];
                if (charIdx < 0) { verts[v] = _originalVertices[m][v]; continue; }

                float charStart = charIdx * charInterval;
                float charEnd = charStart + charDuration;

                // 未入场：隐藏
                if (!forceComplete && _timer < charStart)
                {
                    verts[v] = new Vector3(_localStartPos.x, _localStartPos.y, _originalVertices[m][v].z);
                    if (colors != null && v < colors.Length)
                        colors[v] = new Color32(_originalColors[m][v].r, _originalColors[m][v].g, _originalColors[m][v].b, 0);
                    continue;
                }

                float charProgress = positionCurve.Evaluate(
                    Mathf.Clamp01(Mathf.InverseLerp(charStart, charEnd, _timer))
                );

                // 位置：从起点飞到终点
                Vector3 startLocal = new Vector3(_localStartPos.x, _localStartPos.y, _originalVertices[m][v].z);
                Vector3 target = _originalVertices[m][v];

                if (charIdx < _charCenters.Length)
                {
                    Vector2 finalCenter = _charCenters[charIdx];
                    float charScale = scaleCurve.Evaluate(charProgress);

                    // 缩放目标顶点（围绕最终中心），再 lerp 到缩放后的目标
                    Vector3 scaledTarget = new Vector3(
                        finalCenter.x + (target.x - finalCenter.x) * charScale,
                        finalCenter.y + (target.y - finalCenter.y) * charScale,
                        target.z
                    );
                    Vector3 pos = Vector3.Lerp(startLocal, scaledTarget, charProgress);

                    // 旋转中心 = 从起点到最终中心的动态中点
                    Vector2 startCenter2D = new Vector2(_localStartPos.x, _localStartPos.y);
                    Vector2 rotCenter = Vector2.Lerp(startCenter2D, finalCenter, charProgress);

                    float angle = _charRandomAngle[charIdx] * (1f - charProgress);
                    if (Mathf.Abs(angle) > 0.0001f)
                    {
                        float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                        float rx = pos.x - rotCenter.x, ry = pos.y - rotCenter.y;
                        pos.x = rotCenter.x + rx * cos - ry * sin;
                        pos.y = rotCenter.y + rx * sin + ry * cos;
                    }

                    verts[v] = pos;
                }
                else
                {
                    verts[v] = Vector3.Lerp(startLocal, target, charProgress);
                }
                if (colors != null && v < colors.Length)
                    colors[v] = _originalColors[m][v];
            }

            meshInfo.mesh.vertices = verts;
            meshInfo.mesh.colors32 = colors;
        }

        _text.UpdateVertexData(TMP_VertexDataUpdateFlags.All);
    }

    void UpdateFall(float dt)
    {
        _timer += dt;
        float alpha = 1f - Mathf.Clamp01(_timer / fallFadeTime);
        _text.alpha = color.a * alpha;

        if (_timer >= fallFadeTime)
        {
            _state = State.Done;
            _timer = 0f;
            return;
        }

        TMP_TextInfo textInfo = _text.textInfo;
        for (int m = 0; m < textInfo.meshInfo.Length; m++)
        {
            var meshInfo = textInfo.meshInfo[m];
            if (meshInfo.vertices == null || _originalVertices[m] == null) continue;

            Color32[] colors = meshInfo.mesh.colors32;
            int vCount = meshInfo.vertices.Length;

            for (int v = 0; v < vCount; v++)
            {
                int charIdx = _vertexToChar[m][v];
                if (charIdx < 0 || charIdx >= _visibleCharCount) continue;

                if (colors != null && v < colors.Length)
                    colors[v] = new Color32(
                        _originalColors[m][v].r,
                        _originalColors[m][v].g,
                        _originalColors[m][v].b,
                        (byte)(_originalColors[m][v].a * alpha)
                    );
            }

            meshInfo.mesh.colors32 = colors;
        }

        _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
    }
}
