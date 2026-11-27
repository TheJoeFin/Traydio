using Microsoft.UI.Dispatching;
using System;
using Windows.Foundation;

namespace Trdo.Services;

/// <summary>
/// Whether the app has started to quit, and the guard every <see cref="DispatcherQueueTimer"/>
/// Tick handler goes through.
/// </summary>
/// <remarks>
/// <para>
/// <c>App.ShutdownAndExit()</c> closes the windows and disposes the player, then calls
/// <c>Exit()</c>. WinUI then drains the UI thread's queue
/// (<c>DispatcherQueueController.ShutdownQueue</c>) before the process ends, and timers still
/// running fire during that drain. The teardown before <c>Exit()</c> takes long enough
/// (LibVLC alone can take hundreds of milliseconds) that every timer left running is already due
/// when the drain starts, whatever its interval.
/// </para>
/// <para>
/// A Tick handler that then reaches a closed window or the disposed player throws, and an
/// exception thrown out of a Tick handler fails the process fast from the timer's native callback
/// (<c>DispatcherQueueTimer::TimerCallback</c>, then <c>RoCaptureErrorContext</c>). By then the
/// app's own UnhandledException hooks cannot see it, so it shows up as a Partner Center crash
/// under <c>ShutdownQueue</c> with no managed stack and nothing in traydio.log.
/// </para>
/// </remarks>
internal static class AppLifetime
{
    private const string LogComponent = "Timers";

    private static volatile bool _isShuttingDown;

    /// <summary>True from the moment <c>App.ShutdownAndExit()</c> starts.</summary>
    public static bool IsShuttingDown => _isShuttingDown;

    /// <summary>Called by <c>App.ShutdownAndExit()</c> before it closes or disposes anything.</summary>
    public static void BeginShutdown() => _isShuttingDown = true;

    /// <summary>
    /// For the first line of a Tick handler: once shutdown has begun, stops
    /// <paramref name="timer"/> and returns true, and the handler returns without doing anything.
    /// </summary>
    public static bool StopIfShuttingDown(DispatcherQueueTimer timer)
    {
        if (!_isShuttingDown)
            return false;

        timer.Stop();
        return true;
    }

    /// <summary>
    /// For the catch around a Tick handler's body. Logs the full exception (the stack is the
    /// point: a failfast from a timer callback carries none) and stops the timer, so a fault on a
    /// 16 ms animation frame is one log line rather than sixty a second. Not rethrown: out of a
    /// Tick handler it ends the process, and a missed frame or a dropped delayed click is not
    /// worth that.
    /// </summary>
    public static void ReportTickFailure(DispatcherQueueTimer timer, string component, Exception ex)
    {
        LogService.Error(LogComponent, $"{component} timer tick failed :: {ex}");
        timer.Stop();
    }

    /// <summary>
    /// Wraps <paramref name="tick"/> as a Tick handler with both guards above. For handlers
    /// that are never unsubscribed: a window or control that removes its handler on close keeps
    /// a named method and calls <see cref="StopIfShuttingDown"/> and
    /// <see cref="ReportTickFailure"/> itself, since the delegate made here is a new instance
    /// that a later <c>-=</c> would not match.
    /// </summary>
    public static TypedEventHandler<DispatcherQueueTimer, object> GuardTick(string component, Action tick) =>
        (timer, _) =>
        {
            if (StopIfShuttingDown(timer))
                return;

            try
            {
                tick();
            }
            catch (Exception ex)
            {
                ReportTickFailure(timer, component, ex);
            }
        };
}
