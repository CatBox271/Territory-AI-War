using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using static DeepSeekClient;

public class AIAgent : MonoBehaviour
{
    [SerializeField] private float _cycleInterval = 2f;

    private bool _isRunning;
    private bool _isWaiting;
    private MonoCharacter[] _characters;
    private int _round;
    private bool _start = false;

    #region 初始化

    //工具
    private static readonly ToolDef[] SHOUT_TOOL = new[]
    {
        new ToolDef
        {
            Type = "function",
            Function = new FunctionDef
            {
                Name = "shout_to_other",
                Description = "向另一个AI喊话, 用于挑衅、结盟、威胁等。",
                Parameters = new ParamDef
                {
                    Type = "object",
                    Properties = new Dictionary<string, PropDef>
                    {
                        ["target"] = new() { Type = "integer", Description = "目标AI编号(1-4)，不能是自己" },
                        ["message"] = new() { Type = "string", Description = "喊话内容，最多20字" }
                    },
                    Required = new[] { "target", "message" }
                }
            }
        }
    };
    //初始设定
    private static readonly string[] PERSONALITIES =
    {
        "你是1号AI，性格好斗激进，喜欢挑衅其他AI。你说话简短有力，经常用感叹号。",
        "你是2号AI，性格谨慎保守，说话总是犹豫不决。你喜欢分析利弊",
        "你是3号AI，性格圆滑世故，喜欢结盟和谈条件。你说话礼貌但暗藏心机。",
        "你是4号AI，性格混乱不可预测，经常说莫名其妙的话。你喜欢打断别人，说话跳跃。",
    };
    //初始名字
    private static readonly string[] NAMES = { "1号", "2号", "3号", "4号" };

    void SetDefaultSetting()
    {
        _characters = new MonoCharacter[4];
        for (int i = 0; i < 4; i++)
        {
            _characters[i] = new();
            _characters[i].Creat(NAMES[i],PERSONALITIES[i]);
        }
    }
    private void Awake()
    {
        LoadKeyFromFile();
        SetDefaultSetting();
    }
    #endregion

    #region 循环控制
    private void Start()
    {
        CapturePause.Capture = GetComponent<RenderHeads.Media.AVProMovieCapture.CaptureBase>() ?? FindObjectOfType<RenderHeads.Media.AVProMovieCapture.CaptureBase>();
    }

    private void Update()
    {

        if (CapturePause.IsCapturing)
        {
            if (!_start)
            {
                _start = true;
                StartCycle();
            }
        }
    }

    private void OnDestroy()
    {
        _isRunning = false;
    }
    public void StartCycle()
    {
        if (_isRunning) return;
        _isRunning = true;
        _round = 0;
        RunCycleLoop();
    }
    public void StopCycle()
    {
        _isRunning = false;
    }

    #endregion

    private async void RunCycleLoop()
    {
        while (_isRunning)
        {
            print($"[AIAgent] Round {_round + 1} — rendering for {_cycleInterval}s");
            await Task.Delay((int)(_cycleInterval * 1000));//实际判断延迟
            if (!_isRunning) break;

            if (_isWaiting)
            {
                print("[AIAgent] Previous round still waiting, skipping");
                continue;
            }

            _isWaiting = true;
            _round++;

            CapturePause.Pause();
            //实际处理

            await Task.Delay((int)(_cycleInterval * 1000));//暂时模拟AI延迟

            CapturePause.Resume();
            _isWaiting = false;
        }
    }


    /// <summary>将喊话内容注入其他人的 history</summary>
    private void Broadcast(string content)
    {
        foreach (var mono in _characters)
        {
            mono.AddContent(content);
        }
    }
}
