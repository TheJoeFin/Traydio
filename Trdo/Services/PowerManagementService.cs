using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Trdo.Services;

/// <summary>
/// Holds an explicit system power request while playback is active so the PC
/// stays awake, unless the user opted into allowing sleep. An explicit request
/// is needed because keep-awake was previously an implicit side effect of
/// Media Foundation playback, which no longer applies when LibVLC is the
/// active backend.
/// </summary>
internal static partial class PowerManagementService
{
    private const int POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestSystemRequired = 0;

    private static readonly object _lock = new();
    private static nint _powerRequest;
    private static bool _requestActive;
    private static bool _isPlaybackActive;

    public static void SetPlaybackActive(bool isPlaying)
    {
        lock (_lock)
        {
            _isPlaybackActive = isPlaying;
            Update();
        }
    }

    /// <summary>
    /// Re-evaluates the power request after the AllowSleepWhilePlaying setting changes.
    /// </summary>
    public static void Refresh()
    {
        lock (_lock)
        {
            Update();
        }
    }

    private static void Update()
    {
        bool shouldKeepAwake = _isPlaybackActive && !SettingsService.AllowSleepWhilePlaying;

        LogService.Info("Power",
            $"Update: playing={_isPlaybackActive}, allowSleep={SettingsService.AllowSleepWhilePlaying}, " +
            $"shouldKeepAwake={shouldKeepAwake}, requestActive={_requestActive}");

        try
        {
            if (shouldKeepAwake)
            {
                if (_requestActive)
                    return;

                if (_powerRequest == 0)
                {
                    REASON_CONTEXT context = new()
                    {
                        Version = POWER_REQUEST_CONTEXT_VERSION,
                        Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                        SimpleReasonString = "Playing radio"
                    };

                    _powerRequest = PowerCreateRequest(ref context);
                    if (_powerRequest == 0 || _powerRequest == -1)
                    {
                        Debug.WriteLine($"[PowerManagementService] PowerCreateRequest failed: {Marshal.GetLastWin32Error()}");
                        _powerRequest = 0;
                        return;
                    }
                }

                _requestActive = PowerSetRequest(_powerRequest, PowerRequestSystemRequired);
                Debug.WriteLine($"[PowerManagementService] Keep-awake request set: {_requestActive}");
            }
            else if (_requestActive)
            {
                PowerClearRequest(_powerRequest, PowerRequestSystemRequired);
                _requestActive = false;
                Debug.WriteLine("[PowerManagementService] Keep-awake request cleared");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PowerManagementService] Failed to update power request: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public int Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint powerRequest, int requestType);
}
