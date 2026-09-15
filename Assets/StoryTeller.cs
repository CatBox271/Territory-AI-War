using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;

public class StoryTeller : MonoBehaviour
{
    [Header("相机设置")]
    public Camera scene_camera;
    [Header("背景参数设置")]
    public SpriteRenderer BackgroundSprite;
    public float BackgroundDuration = 0.5f;
    public float BackgroundBlur = 12f;
    public float BackgroundTransparent = 0.75f;
    public AnimationCurve BackgroundCurve = AnimationCurve.Linear(0, 0, 1, 1);
    private bool IsBackground = false;
    public bool test;
    public bool testMove;
    [Header("场景")]
    public static StoryTeller Instance;

    public GameObject GoText;
    public GameObject GoPicture;

    private Vector3 LeftBottonPos;
    private Vector3 RightTopPos;
    private float Width;
    private float Height;
    [Header("TestCircle")]
    public Transform circle;
    [Header("录制和内置时钟")]
    public float CaptureFrame = 60f;
    private float _CaptureDeltaTime = float.NaN;

    private float CaptureDeltaTime {
        get
        {
            if (float.IsNaN(_CaptureDeltaTime))
            {
                _CaptureDeltaTime = 1f / CaptureFrame;
            }
            return _CaptureDeltaTime;
        }
    }
    private int frame = 0;
    private static bool stop = false;

    #region 初始化

    private void Awake()
    {
        Instance = this;
        stop = false;
        Time.timeScale = 1;
        LeftBottonPos = scene_camera.ScreenToWorldPoint(new());
        RightTopPos = scene_camera.ScreenToWorldPoint(new(scene_camera.pixelWidth,scene_camera.pixelHeight));
        Vector3 v3 = RightTopPos - LeftBottonPos;
        CapturePause.Capture.CaptureUpdate += CaptureUpdate;//在CapturePause.Capture里注册时钟
        _ = RealTimeUpdate();
    }
    private void OnValidate()
    {
        if (test)
        {
            OpenScene();
            Time.timeScale = 0;
        }
        else
        {
            CloseScene();
            Time.timeScale = 1;
        }
        if (testMove)
        {
            StartCoroutine(Move());
        }
    }
    #endregion

    #region 时钟
    public void CaptureUpdate()
    {
        frame++;
        print("scene_clock");
    }
    IEnumerator WaitForCaptureUpdate()
    {
        int lastframe = frame;
        yield return new WaitUntil(() => frame > lastframe);
    }

    private async Task RealTimeUpdate()
    {
        while (!stop)
        {
            await Task.Delay((int)(CaptureDeltaTime * 1000));
            if (!CapturePause.Capture.IsCapturing()) CaptureUpdate();
        }
    }
    private void OnDestroy()
    {
        stop = true;
        print("SceneClockStop");
    }
    private void OnApplicationQuit()
    {
        stop = true;
        print("SceneClockStop");
    }
    #endregion

    #region 场景支持
    //将包含图片,文本 编排位置像js先分割位置。
    public enum Direction
    { 
        Left,
        Right,
        Top,
        Botton,
    }
    public class Area
    {
        public Direction direction;
        public List<Item> items;
    }
    public enum ItemType
    { 
        Text,
        Picture,
    }
    public class Item
    {
        public ItemType type;
        public GameObject realOb;
        public string str1 = null;//Text content|Picture path
        public Item() { }
        public Item(ItemType type)
        {
            this.type = type;
            TryInstanceGo(ref realOb);
        }
        public Item(string str)
        {
            type = ItemType.Text;
            str1 = str;
            _ = type switch
            {
                ItemType.Text => CreatTextGo(),
                ItemType.Picture => CreatPictureGo(),
            };

        }
        public void Delete()
        {
            if(realOb != null) Destroy(realOb);
        }
        public bool TryInstanceGo(ref GameObject go)
        {
            if (Instance == null) return false;
            go = Instantiate(type switch
            {
                ItemType.Text => Instance.GoText,
                ItemType.Picture => Instance.GoPicture,
            },
            Instance.transform);
            return true;
        }

        public bool CreatTextGo()
        {
            if (!TryInstanceGo(ref realOb)) return false;
            var text = realOb.GetComponent<TextMeshPro>();
            text.text = str1;
            return true;
        }



        public bool CreatPictureGo()
        {
            if (!TryInstanceGo(ref realOb)) return false;
            var sprite = realOb.GetComponent<SpriteRenderer>();
            sprite.sprite = Resources.Load(str1) as Sprite;
            return true;
        }

        #endregion
        #region style
        public Vector4 border = Vector4.zero;
        public float _border { set { border = Vector4.one * value; } }
        public Vector4 margin = Vector4.zero;
        public float _margin { set { margin = Vector4.one * value; } }
        public Vector2 offset = Vector2.zero;
        public Vector3 eulerAngle = Vector3.zero;
        public Vector2 size = new();
        public float width => size.x;
        public float height => size.y;
        #endregion
    }
    IEnumerator Move()
    {
        float t = 0;

        while (true)
        {
            circle.position = Vector3.Lerp(LeftBottonPos, RightTopPos, t);
            if (t >= 1) break;
            yield return WaitForCaptureUpdate();
            t += CaptureDeltaTime;
        }
    }

    #region 场景出入管理
    public void OpenScene(bool immediately = false)
    {
        if (IsBackground) return;
        IsBackground = true;
        scene_camera.enabled = true;
        ClearScene(true);
        StartCoroutine(EBackGroundSwitch(BackgroundDuration, true));
    }
    public void CloseScene(bool immediately = false)
    {
        if (!IsBackground) return;
        IsBackground = false;
        StartCoroutine(ECloseScene(immediately));
    }
    IEnumerator ECloseScene(bool immediately = false)
    {
        ClearScene(immediately);
        yield return EBackGroundSwitch(BackgroundDuration, false);
        scene_camera.enabled = false;
    }
    public void ClearScene(bool immediately = false)
    {
        
    }
    //我需要一个在timeScale = 还能运行的动画计时器。或许要依赖record? ok我做好了
    IEnumerator EBackGroundSwitch(float time ,bool on)
    {
        float BlurEnd = on ? BackgroundBlur : 0;
        float AlphaEnd = on ? BackgroundTransparent : 0;
        Color col = BackgroundSprite.color;
        if (time <= 0)
        {
            BackgroundSprite.sharedMaterial.SetFloat("_Size", BlurEnd);
            col.a = AlphaEnd;
            BackgroundSprite.color = col;
        }
        else
        {
            float BlurStart = BackgroundSprite.sharedMaterial.GetFloat("_Size");
            float AlphaStart = col.a;
            float t = 0;
            time = Mathf.Max(0.1f, time);
            while (true)
            {
                float curveT = BackgroundCurve.Evaluate(t);
                BackgroundSprite.sharedMaterial.SetFloat("_Size", Mathf.Lerp(BlurStart, BlurEnd, curveT));
                col.a = Mathf.Lerp(AlphaStart, AlphaEnd, curveT);
                BackgroundSprite.color = col;
                if (t >= 1) break;
                yield return WaitForCaptureUpdate();
                t += CaptureDeltaTime / time;
            }
        }
    }

    #endregion
}
