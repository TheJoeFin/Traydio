using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using WinUIEx;

namespace Trdo.Services;

internal static partial class WindowPlacementService
{
    private const int WindowMargin = 12;
    private static PointInt32? _lastAnchorPoint;
    private static ShellTrayIcon? _trayIcon;

    public static void CapturePointerAnchor()
    {
        if (PInvoke.GetCursorPos(out Point point))
            _lastAnchorPoint = new PointInt32(point.X, point.Y);
    }

    /// <summary>
    /// Discards any captured pointer anchor so the next placement falls back
    /// to the tray icon (or taskbar) rect instead of a cursor position.
    /// </summary>
    /// <remarks>
    /// Touch and pen taps on the notification icon activate it without ever
    /// warping the hardware cursor there, since the taskbar handles those
    /// pointer types directly rather than through the legacy mouse-message
    /// path. <see cref="CapturePointerAnchor"/> would then capture whatever
    /// stale position the real mouse was last at, which is almost never over
    /// the icon — sending placement down the pointer-offset branch in
    /// <see cref="PositionWindowNearAnchor"/> instead of the icon-centered
    /// one, and landing the window somewhere unrelated to the tap. Callers
    /// that already know the invocation came from the tray icon should call
    /// this instead of <see cref="CapturePointerAnchor"/> so placement always
    /// derives from the icon's actual geometry.
    /// </remarks>
    public static void ClearPointerAnchor()
    {
        _lastAnchorPoint = null;
    }

    public static void SetTrayIconSource(ShellTrayIcon trayIcon)
    {
        _trayIcon = trayIcon;
    }

    public static void PositionWindowNearAnchor(Window window, int width, int height)
    {
        bool usePointerPlacement = _lastAnchorPoint is PointInt32;
        PointInt32 anchor = GetAnchorPoint();
        DisplayArea? displayArea = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
        RectInt32 workArea = displayArea?.WorkArea ?? DisplayArea.Primary.WorkArea;

        // Win32 and WinUI positioning APIs all use physical pixels. Scale the
        // caller's logical width/height so placement and clamping are correct
        // at any DPI (125%, 150%, 200%, etc.). The DPI must come from the
        // anchor's monitor, not the window: a hidden window keeps the DPI of
        // wherever it last was, which goes stale across monitor/scale changes.
        uint dpi = GetDpiForAnchor(anchor, window);
        // A size remembered from a larger monitor must not overhang this one.
        int physWidth = System.Math.Min(ToPhysical(width, dpi), workArea.Width);
        int physHeight = System.Math.Min(ToPhysical(height, dpi), workArea.Height);

        int x;
        int y;

        // When the pointer is over the tray icon, center on the icon rather than
        // offset from the cursor — matches native Windows tray flyout behavior.
        bool trayIconAvailable = TryGetTrayIconRect(out RECT iconRect);
        bool pointerIsOverTrayIcon = usePointerPlacement && trayIconAvailable
            && anchor.X >= iconRect.left && anchor.X <= iconRect.right
            && anchor.Y >= iconRect.top && anchor.Y <= iconRect.bottom;

        if (usePointerPlacement && !pointerIsOverTrayIcon)
        {
            bool placeLeft = anchor.X >= workArea.X + (workArea.Width / 2);
            bool placeAbove = anchor.Y >= workArea.Y + (workArea.Height / 2);

            x = placeLeft ? anchor.X - physWidth - WindowMargin : anchor.X + WindowMargin;
            y = placeAbove ? anchor.Y - physHeight - WindowMargin : anchor.Y + WindowMargin;
        }
        else if (trayIconAvailable)
        {
            int iconCenterX = (iconRect.left + iconRect.right) / 2;
            x = iconCenterX - (physWidth / 2);

            if (TryGetTaskbarRect(out RECT taskbarRect, out uint taskbarEdge))
            {
                y = taskbarEdge switch
                {
                    ABE_BOTTOM => taskbarRect.top - physHeight,
                    ABE_TOP => taskbarRect.bottom,
                    _ => iconRect.top >= workArea.Y + (workArea.Height / 2)
                        ? iconRect.top - physHeight - WindowMargin
                        : iconRect.bottom + WindowMargin
                };
            }
            else
            {
                bool iconOnBottomHalf = iconRect.top >= workArea.Y + (workArea.Height / 2);
                y = iconOnBottomHalf
                    ? iconRect.top - physHeight - WindowMargin
                    : iconRect.bottom + WindowMargin;
            }
        }
        else if (TryGetTaskbarRect(out RECT taskbarRect, out uint taskbarEdge))
        {
            bool isRtl = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
            int taskbarMidY = taskbarRect.top + ((taskbarRect.bottom - taskbarRect.top) / 2);

            x = taskbarEdge switch
            {
                ABE_BOTTOM or ABE_TOP => isRtl
                    ? taskbarRect.left + WindowMargin
                    : taskbarRect.right - WindowMargin - physWidth,
                _ => workArea.X + (workArea.Width / 2) - (physWidth / 2)
            };

            y = taskbarEdge switch
            {
                ABE_BOTTOM => taskbarRect.top - physHeight,
                ABE_TOP => taskbarRect.bottom,
                ABE_LEFT => taskbarMidY - (physHeight / 2),
                ABE_RIGHT => taskbarMidY - (physHeight / 2),
                _ => taskbarRect.top - physHeight
            };

            if (taskbarEdge is ABE_LEFT or ABE_RIGHT)
            {
                x = taskbarEdge == ABE_LEFT
                    ? taskbarRect.right + WindowMargin
                    : taskbarRect.left - physWidth - WindowMargin;
            }
        }
        else
        {
            bool placeAbove = anchor.Y >= workArea.Y + (workArea.Height / 2);

            x = anchor.X - (physWidth / 2);
            y = placeAbove ? anchor.Y - physHeight - WindowMargin : anchor.Y + WindowMargin;
        }

        int maxX = System.Math.Max(workArea.X, workArea.X + workArea.Width - physWidth);
        int maxY = System.Math.Max(workArea.Y, workArea.Y + workArea.Height - physHeight);

        x = System.Math.Clamp(x, workArea.X, maxX);
        y = System.Math.Clamp(y, workArea.Y, maxY);

        window.AppWindow.MoveAndResize(new RectInt32(x, y, physWidth, physHeight));
    }

    /// <summary>
    /// Puts a window back where it was left, nudged fully inside that display's work area.
    /// Returns false, moving nothing, when the spot is no longer on any display - a monitor
    /// that has been unplugged, say - so the caller can place the window afresh.
    /// </summary>
    public static bool TryRestoreBounds(Window window, SavedWindowBounds saved)
    {
        DisplayArea? displayArea = DisplayArea.GetFromRect(
            new RectInt32(saved.X, saved.Y, saved.Width, saved.Height),
            DisplayAreaFallback.None);
        if (displayArea is null)
            return false;

        RectInt32 workArea = displayArea.WorkArea;
        PointInt32 probe = new(
            System.Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - 1),
            System.Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - 1));

        uint dpi = GetDpiForAnchor(probe, window);
        int physWidth = System.Math.Min(ToPhysical(saved.Width, dpi), workArea.Width);
        int physHeight = System.Math.Min(ToPhysical(saved.Height, dpi), workArea.Height);

        int x = System.Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - physWidth);
        int y = System.Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - physHeight);

        // Move before sizing: arriving on a monitor with a different scale rescales the
        // window, which would otherwise be applied on top of the size computed for it.
        window.AppWindow.Move(new PointInt32(x, y));
        window.AppWindow.Resize(new SizeInt32(physWidth, physHeight));
        return true;
    }

    /// <summary>
    /// Positions a transient overlay window (e.g. the song-change popup) at
    /// the bottom-center of the work area belonging to the taskbar/tray
    /// monitor — not necessarily the primary display or whichever monitor the
    /// window last lived on. Falls back to the last pointer/tray anchor, then
    /// the primary display, if no taskbar can be located.
    /// </summary>
    public static void PositionWindowBottomCenter(Window window, int width, int height, int bottomMargin = 0)
    {
        RectInt32 bounds = GetBottomCenterPlacement(window, width, height, bottomMargin, out _);
        window.AppWindow.MoveAndResize(bounds);
    }

    /// <summary>
    /// Computes — but does not apply — the bottom-center placement described on
    /// <see cref="PositionWindowBottomCenter"/>, in physical pixels, along with
    /// the DPI of the monitor it was computed for. Callers that animate a
    /// window into place need the target rect up front so they can offset from
    /// it, and the DPI so their offsets scale with the monitor.
    /// </summary>
    /// <param name="bottomMargin">
    /// Logical-pixel gap to leave between the bottom of the window and the
    /// bottom of the work area (i.e. the top of the taskbar).
    /// </param>
    public static RectInt32 GetBottomCenterPlacement(
        Window window,
        int width,
        int height,
        int bottomMargin,
        out uint dpi)
    {
        RectInt32 workArea;
        PointInt32 dpiProbePoint;

        if (TryGetTaskbarRect(out RECT taskbarRect, out _))
        {
            PointInt32 taskbarPoint = new(
                (taskbarRect.left + taskbarRect.right) / 2,
                (taskbarRect.top + taskbarRect.bottom) / 2);
            DisplayArea? taskbarDisplay = DisplayArea.GetFromPoint(taskbarPoint, DisplayAreaFallback.Nearest);
            workArea = taskbarDisplay?.WorkArea ?? DisplayArea.Primary.WorkArea;
            dpiProbePoint = taskbarPoint;
        }
        else
        {
            PointInt32 anchor = GetAnchorPoint();
            DisplayArea? displayArea = DisplayArea.GetFromPoint(anchor, DisplayAreaFallback.Nearest);
            workArea = displayArea?.WorkArea ?? DisplayArea.Primary.WorkArea;
            dpiProbePoint = anchor;
        }

        // Physical pixels, scaled by the taskbar monitor's DPI — see the
        // remarks on PositionWindowNearAnchor for why the anchor's monitor
        // (not the window's) must supply the DPI.
        dpi = GetDpiForAnchor(dpiProbePoint, window);
        int physWidth = ToPhysical(width, dpi);
        int physHeight = ToPhysical(height, dpi);
        int physBottomMargin = (int)(bottomMargin * dpi / 96.0);

        int x = workArea.X + (workArea.Width / 2) - (physWidth / 2);
        int y = workArea.Y + workArea.Height - physHeight - physBottomMargin;

        int maxX = System.Math.Max(workArea.X, workArea.X + workArea.Width - physWidth);
        int maxY = System.Math.Max(workArea.Y, workArea.Y + workArea.Height - physHeight);

        x = System.Math.Clamp(x, workArea.X, maxX);
        y = System.Math.Clamp(y, workArea.Y, maxY);

        return new RectInt32(x, y, physWidth, physHeight);
    }

    /// <summary>
    /// Converts a logical (DIP) extent to physical pixels, rounding *up*. A
    /// window sized from measured content must never end up a fraction of a
    /// pixel shorter than that content: at fractional scales (125%, 150%) a
    /// truncating cast loses up to a pixel, and windows that hug their content
    /// pay for it with clipped text.
    /// </summary>
    private static int ToPhysical(int logical, uint dpi) =>
        (int)System.Math.Ceiling(logical * dpi / 96.0);

    private static uint GetDpiForAnchor(PointInt32 anchor, Window window)
    {
        Point point = new() { X = anchor.X, Y = anchor.Y };
        nint monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor != 0
            && PInvoke.GetDpiForMonitor((HMONITOR)monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
            && dpiX != 0)
        {
            return dpiX;
        }

        uint dpi = PInvoke.GetDpiForWindow((HWND)window.GetWindowHandle());
        return dpi == 0 ? 96u : dpi;
    }

    private static PointInt32 GetAnchorPoint()
    {
        if (_lastAnchorPoint is PointInt32 anchor)
            return anchor;

        if (TryGetTrayIconAnchorPoint(out anchor))
            return anchor;

        if (TryGetTaskbarAnchorPoint(out anchor))
            return anchor;

        RectInt32 workArea = DisplayArea.Primary.WorkArea;
        return new PointInt32(
            workArea.X + (workArea.Width / 2),
            workArea.Y + workArea.Height - WindowMargin);
    }

    private static bool TryGetTrayIconAnchorPoint(out PointInt32 anchor)
    {
        if (!TryGetTrayIconRect(out RECT iconRect))
        {
            anchor = default;
            return false;
        }

        anchor = new PointInt32(
            (iconRect.left + iconRect.right) / 2,
            (iconRect.top + iconRect.bottom) / 2);
        return true;
    }

    private static bool TryGetTrayIconRect(out RECT iconRect)
    {
        iconRect = default;

        if (_trayIcon is null)
            return false;

        // Read per call: the icon falls back from GUID to uID identity if the shell refuses the GUID.
        ShellTrayIconIdentity identity = _trayIcon.Identity;
        if (identity.WindowHandle == 0)
            return false;

        NOTIFYICONIDENTIFIER identifier = new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
        };

        // The shell wants the icon identified one way or the other, not both.
        if (identity.Guid is Guid guid)
        {
            identifier.guidItem = guid;
        }
        else
        {
            identifier.hWnd = (HWND)identity.WindowHandle;
            identifier.uID = identity.Id;
        }

        return PInvoke.Shell_NotifyIconGetRect(in identifier, out iconRect) == 0;
    }

    private static bool TryGetTaskbarRect(out RECT rc, out uint edge)
    {
        APPBARDATA appBarData = new()
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
        };

        if (PInvoke.SHAppBarMessage(ABM_GETTASKBARPOS, ref appBarData) == 0)
        {
            rc = default;
            edge = 0;
            return false;
        }

        rc = appBarData.rc;
        edge = appBarData.uEdge;
        return true;
    }

    private static bool TryGetTaskbarAnchorPoint(out PointInt32 anchor)
    {
        if (!TryGetTaskbarRect(out RECT taskbarRect, out uint edge))
        {
            anchor = default;
            return false;
        }

        bool isRtl = CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;
        int taskbarMidY = taskbarRect.top + ((taskbarRect.bottom - taskbarRect.top) / 2);

        anchor = edge switch
        {
            ABE_BOTTOM => new PointInt32(
                isRtl ? taskbarRect.left + WindowMargin : taskbarRect.right - WindowMargin,
                taskbarMidY),
            ABE_TOP => new PointInt32(
                isRtl ? taskbarRect.left + WindowMargin : taskbarRect.right - WindowMargin,
                taskbarRect.bottom - WindowMargin),
            ABE_LEFT => new PointInt32(
                taskbarRect.right - WindowMargin,
                taskbarRect.bottom - WindowMargin),
            ABE_RIGHT => new PointInt32(
                taskbarRect.left + WindowMargin,
                taskbarRect.bottom - WindowMargin),
            _ => new PointInt32(
                isRtl ? taskbarRect.left + WindowMargin : taskbarRect.right - WindowMargin,
                taskbarRect.bottom - WindowMargin)
        };

        return true;
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MDT_EFFECTIVE_DPI = 0;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;

}
