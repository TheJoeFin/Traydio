using LibVLCSharp.Shared;
using System;
using System.Diagnostics;

namespace Trdo.Services.Playback;

/// <summary>
/// Manages the shared LibVLC native instance for fallback playback.
/// </summary>
public static class LibVlcHost
{
    private static LibVLC? _instance;
    private static LibVlcLogCapture? _logCapture;
    private static bool _initializeAttempted;

    public static LibVLC? Instance => _instance;

    /// <summary>
    /// The capture of LibVLC's native log, used to explain playback failures.
    /// Null until <see cref="TryInitialize"/> succeeds.
    /// </summary>
    public static LibVlcLogCapture? LogCapture => _logCapture;

    public static bool IsAvailable => _instance is not null;

    public static bool TryInitialize()
    {
        if (_initializeAttempted)
        {
            return _instance is not null;
        }

        _initializeAttempted = true;

        try
        {
            Core.Initialize();

            // --verbose=1 raises LibVLC's internal verbosity to warnings so the log
            // callback actually receives the lines explaining a failed open. Debug level
            // (2) is far too chatty to run permanently on a live audio path.
            _instance = new LibVLC("--no-video", "--verbose=1");

            // Without this, LibVLC's WASAPI output leaves its session display name unset,
            // so Windows falls back to its own default ("VLC media player (LibVLC ...)")
            // instead of grouping the session under Traydio - producing a second, oddly
            // named entry in the volume mixer for what is really the same app (see issue #133).
            _instance.SetUserAgent("Traydio", "Traydio");

            _logCapture = new LibVlcLogCapture(_instance);

            LogService.Info("LibVlcHost", $"LibVLC initialized (version {_instance.Version})");
            Debug.WriteLine("[LibVlcHost] LibVLC initialized");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("LibVlcHost",
                $"LibVLC unavailable on {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; " +
                "native libvlc.dll failed to load", ex);
            Debug.WriteLine($"[LibVlcHost] Failed to initialize LibVLC: {ex.Message}");
            _instance = null;
            return false;
        }
    }

    public static void Dispose()
    {
        // Detach the log callback before the native instance goes away, so a callback
        // in flight can't land on freed memory.
        _logCapture?.Dispose();
        _logCapture = null;
        _instance?.Dispose();
        _instance = null;
        _initializeAttempted = false;
    }
}
