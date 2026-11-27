using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Trdo.Services.Playback;

/// <summary>
/// Captures LibVLC's own native log so playback failures can be explained.
/// <para>
/// Without this, the only failure signal LibVLC gives the app is the
/// <c>EncounteredError</c> event, which carries no payload at all — every failure
/// looked identical ("LibVLC playback error") no matter whether the host did not
/// resolve, the server returned 404, or the codec was unsupported. LibVLC knows the
/// real reason and writes it to its log ("access error: HTTP answer code 404",
/// "main input error: Your input can't be opened"); this class keeps the recent tail
/// of that log so the reason can be attached to the failure and written to
/// <see cref="LogService"/>.
/// </para>
/// <para>
/// Log callbacks arrive on LibVLC's own native threads, so everything here is
/// lock-guarded, allocation-light, and must never throw back into native code.
/// </para>
/// </summary>
public sealed partial class LibVlcLogCapture : IDisposable
{
    /// <summary>How many recent warning/error lines to retain for diagnostics.</summary>
    private const int MaxRetainedLines = 60;

    /// <summary>Cap on a single retained line, so a pathological log can't balloon memory.</summary>
    private const int MaxLineLength = 400;

    private readonly LibVLC _libVlc;
    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();

    private string? _lastError;
    private bool _disposed;

    public LibVlcLogCapture(LibVLC libVlc)
    {
        _libVlc = libVlc ?? throw new ArgumentNullException(nameof(libVlc));

        try
        {
            _libVlc.Log += OnLog;
        }
        catch (Exception ex)
        {
            // Log capture is diagnostics only — never let it stop playback from working.
            LogService.Warn("LibVlcLogCapture", $"Could not attach to the LibVLC log: {ex.Message}");
            _disposed = true;
        }
    }

    /// <summary>
    /// The most recent error-level line LibVLC produced, or <c>null</c> when it has not
    /// reported one since the last <see cref="Reset"/>.
    /// </summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>
    /// Clears the retained lines. Called at the start of each prepare so a failure is
    /// explained by this attempt's log rather than a previous station's.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _recent.Clear();
            _lastError = null;
        }
    }

    /// <summary>Returns the retained warning/error lines, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    /// <summary>
    /// Builds a short, single-line reason suitable for a user-facing error message,
    /// preferring the last error line and falling back to the last retained line.
    /// Returns <c>null</c> when LibVLC logged nothing useful.
    /// </summary>
    public string? BuildFailureReason()
    {
        lock (_gate)
        {
            string? reason = _lastError;
            if (string.IsNullOrWhiteSpace(reason) && _recent.Count > 0)
            {
                reason = _recent.Last();
            }

            return string.IsNullOrWhiteSpace(reason) ? null : reason;
        }
    }

    /// <summary>
    /// Writes the retained lines to the app log under the given context. Called when a
    /// LibVLC attempt fails, so the log file explains the failure without needing a debugger.
    /// </summary>
    public void DumpTo(string context)
    {
        IReadOnlyList<string> lines = Snapshot();
        if (lines.Count == 0)
        {
            LogService.Warn("LibVlcLogCapture", $"{context}: LibVLC produced no warning or error lines");
            return;
        }

        StringBuilder sb = new();
        sb.Append(context).Append(": last ").Append(lines.Count).Append(" LibVLC log line(s):");
        foreach (string line in lines)
        {
            sb.Append("\n    ").Append(line);
        }

        LogService.Warn("LibVlcLogCapture", sb.ToString());
    }

    private void OnLog(object? sender, LogEventArgs e)
    {
        // Runs on a native LibVLC thread. Never throw from here.
        try
        {
            if (_disposed || e.Level < LogLevel.Warning)
            {
                return;
            }

            string module = string.IsNullOrWhiteSpace(e.Module) ? "vlc" : e.Module;
            string message = e.Message ?? string.Empty;
            if (message.Length > MaxLineLength)
            {
                message = message[..MaxLineLength] + "…";
            }

            string line = $"{e.Level.ToString().ToLowerInvariant()}: {module}: {message}";

            lock (_gate)
            {
                _recent.Enqueue(line);
                while (_recent.Count > MaxRetainedLines)
                {
                    _recent.Dequeue();
                }

                if (e.Level == LogLevel.Error)
                {
                    _lastError = $"{module}: {message}";
                }
            }

            Debug.WriteLine($"[LibVLC] {line}");
        }
        catch
        {
            // Diagnostics must never destabilise the native callback.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _libVlc.Log -= OnLog;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibVlcLogCapture] Error detaching log handler: {ex.Message}");
        }

        Reset();
    }
}
