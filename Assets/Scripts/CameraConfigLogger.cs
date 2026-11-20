using System;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// 기기가 지원하는 AR 카메라 설정(해상도, 비율, FPS) 목록을 로그로 출력하고,
/// 16:9 설정(1280x720 우선, 없으면 가장 가까운 16:9)으로 변경한다.
/// </summary>
public class CameraConfigLogger : MonoBehaviour
{
    [SerializeField] ARCameraManager cameraManager;

    [Header("Preferred Configuration")]
    [SerializeField] int preferredWidth = 1280;
    [SerializeField] int preferredHeight = 720;

    bool handled;

    private void Awake()
    {
        if (cameraManager == null) cameraManager = GetComponent<ARCameraManager>();
        if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
    }

    private void OnEnable()
    {
        if (cameraManager != null) cameraManager.frameReceived += OnFrameReceived;
        else Debug.LogWarning("[CameraConfig] ARCameraManager not found.");
    }

    private void OnDisable()
    {
        if (cameraManager != null) cameraManager.frameReceived -= OnFrameReceived;
    }

    // 카메라 서브시스템이 실행 중이어야 목록을 얻을 수 있으므로 첫 프레임 수신 후 한 번만 처리
    private void OnFrameReceived(ARCameraFrameEventArgs args)
    {
        if (handled) return;
        handled = true;
        cameraManager.frameReceived -= OnFrameReceived;
        LogAndSelectConfiguration();
    }

    private void LogAndSelectConfiguration()
    {
        var sb = new StringBuilder();
        float screenLong = Mathf.Max(Screen.width, Screen.height);
        float screenShort = Mathf.Min(Screen.width, Screen.height);
        sb.AppendLine("[CameraConfig] ===== Camera configurations =====");
        sb.AppendLine($"Screen: {Screen.width}x{Screen.height} (long/short = {screenLong / screenShort:F3}), orientation: {Screen.orientation}");

        var subsystem = cameraManager.subsystem;
        if (subsystem == null || !subsystem.subsystemDescriptor.supportsCameraConfigurations)
        {
            sb.AppendLine("Camera configurations are not supported on this device/provider.");
            Debug.Log(sb.ToString());
            return;
        }

        var current = cameraManager.currentConfiguration;
        sb.AppendLine($"Current: {(current.HasValue ? Describe(current.Value) : "none")}");

        XRCameraConfiguration? selected = null;
        using (NativeArray<XRCameraConfiguration> configs = cameraManager.GetConfigurations(Allocator.Temp))
        {
            sb.AppendLine($"Supported configurations ({configs.Length}):");
            for (int i = 0; i < configs.Length; i++)
            {
                var config = configs[i];
                bool isCurrent = current.HasValue && current.Value == config;
                sb.AppendLine($"  [{i}] {Describe(config)}{(isCurrent ? "  <= current" : "")}");
            }

            selected = SelectConfiguration(configs);
        }

        Debug.Log(sb.ToString());
        ApplyConfiguration(selected, current);
    }

    // 선호 해상도(기본 1280x720)가 있으면 그것을, 없으면 픽셀 수가 가장 가까운 16:9 설정을 고른다
    private XRCameraConfiguration? SelectConfiguration(NativeArray<XRCameraConfiguration> configs)
    {
        long preferredPixels = (long)preferredWidth * preferredHeight;
        XRCameraConfiguration? best = null;
        long bestDiff = long.MaxValue;

        foreach (var config in configs)
        {
            if (!Is16By9(config.width, config.height)) continue;

            if (Matches(config, preferredWidth, preferredHeight)) return config;

            long diff = Math.Abs((long)config.width * config.height - preferredPixels);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = config;
            }
        }
        return best;
    }

    private void ApplyConfiguration(XRCameraConfiguration? selected, XRCameraConfiguration? current)
    {
        if (!selected.HasValue)
        {
            Debug.LogWarning("[CameraConfig] No 16:9 configuration available. Keeping the current configuration.");
            return;
        }

        if (current.HasValue && current.Value == selected.Value)
        {
            Debug.Log($"[CameraConfig] Already using {Describe(selected.Value)}. No change needed.");
            return;
        }

        try
        {
            cameraManager.currentConfiguration = selected.Value;
            Debug.Log($"[CameraConfig] Switched configuration to {Describe(selected.Value)}.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CameraConfig] Failed to set configuration {Describe(selected.Value)}: {e.Message}");
        }
    }

    private static bool Matches(XRCameraConfiguration config, int width, int height)
    {
        // 기기 방향에 따라 가로/세로가 뒤바뀌어 보고될 수 있으므로 양쪽 모두 허용
        return (config.width == width && config.height == height) ||
               (config.width == height && config.height == width);
    }

    private static bool Is16By9(int w, int h)
    {
        int longSide = Mathf.Max(w, h);
        int shortSide = Mathf.Min(w, h);
        return shortSide > 0 && longSide * 9 == shortSide * 16;
    }

    private static string Describe(XRCameraConfiguration config)
    {
        int w = config.width;
        int h = config.height;
        float aspect = h > 0 ? (float)Mathf.Max(w, h) / Mathf.Min(w, h) : 0f;
        string fps = config.framerate.HasValue ? $"{config.framerate.Value}fps" : "fps unknown";
        return $"{w}x{h} (ratio {ReduceRatio(w, h)} = {aspect:F3}), {fps}, depth: {config.depthSensorSupported}";
    }

    private static string ReduceRatio(int w, int h)
    {
        int a = w, b = h;
        while (b != 0) { int t = a % b; a = b; b = t; }
        return a == 0 ? "?" : $"{w / a}:{h / a}";
    }
}
