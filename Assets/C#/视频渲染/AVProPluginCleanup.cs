using RenderHeads.Media.AVProMovieCapture;
using UnityEngine;

/// <summary>
/// 手动释放 AVPro Movie Capture 原生插件内存。
/// 停止录制后如果 Unity 内存仍居高不下，调用 DeinitPlugin() 彻底释放 NativePlugin 全局资源。
/// 注意：Deinit 后如需再次录制，需要重新初始化插件（例如禁用再启用 Capture 组件或重载场景）。
/// </summary>
public class AVProPluginCleanup : MonoBehaviour
{
    [ContextMenu("Deinit AVPro NativePlugin")]
    public void DeinitPlugin()
    {
        NativePlugin.Deinit();
        Debug.Log("[AVProPluginCleanup] NativePlugin.Deinit() 已调用，AVPro 原生资源已释放。");
    }
}
