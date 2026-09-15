using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Diagnostics;
using Trdo.Pages;
using Trdo.Services;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using WinUIEx;

namespace Trdo.Controls;

/// <summary>
/// Borderless popup window shown near the tray icon / click position,
/// replacing the WinUIEx tray flyout. Light-dismisses when focus leaves.
/// </summary>
public sealed partial class TrayPopupWindow : WindowEx
{
    private const int PopupWidth = 320;
    private const int PopupHeight = 500;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint GA_ROOT = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // Same slide + fade entrance/exit feel as SongChangePopupWindow, driven by a
    // frame timer over window position and layered alpha rather than a XAML
    // storyboard: MicaBackdrop paints behind the XAML content and is unaffected
    // by UIElement.Opacity, so fading the surface requires per-window alpha.
    private const int ShowSlideDistance = 24;
    private const int HideSlideDistance = 12;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(15);
    private const double ShowDurationMs = 250;
    private const double HideDurationMs = 180;

    /// <summary>
    /// A tray click that arrives right after a light-dismiss hide is the same
    /// click that dismissed the popup — treat it as "close" instead of re-showing.
    /// </summary>
    private static readonly TimeSpan RecentDismissWindow = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly HWND _hwnd;
    private ShellPage? _shellPage;
    private bool _isShowing;
    private bool _isPopupVisible;
    private DateTime _lastDismissedAtUtc = DateTime.MinValue;

    // Animation state, mirroring SongChangePopupWindow: _baseY is the popup's
    // resting Y; the slide animates an offset below it, in physical pixels
    // scaled to the target monitor.
    private bool _isHiding;
    private int _baseX;
    private int _baseY;
    private int _fromOffset;
    private int _toOffset;
    private double _fromOpacity;
    private double _toOpacity;
    private double _durationMs = ShowDurationMs;

    // Where the last rendered frame left the popup, so an animation that
    // interrupts another can start from the current state instead of snapping.
    private int _currentOffset;
    private double _currentOpacity;

    public TrayPopupWindow()
    {
        InitializeComponent();

        _hwnd = (HWND)WindowNative.GetWindowHandle(this);

        AppWindow.SetIcon("Assets\\Radio.ico");
        ConfigurePresenter();
        // ConfigureToolWindow();

        Activated += OnWindowActivated;

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(150);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideIfFocusLeftWindow();

        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = FrameInterval;
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += AnimationTimer_Tick;
    }

    public bool IsPopupVisible => _isPopupVisible;

    /// <summary>
    /// Shows the popup near the last captured pointer anchor, or hides it when
    /// it is already visible (or was just light-dismissed by the same click).
    /// </summary>
    /// <param name="clickedAtUtc">
    /// When the click that asked for this happened. Usually now, but a tray click that had to
    /// wait out the double-click interval is older than that, and the "same click that
    /// dismissed us" check has to be made against the click, not against when it was acted on.
    /// </param>
    public void ToggleNearAnchor(DateTime? clickedAtUtc = null)
    {
        if (_isPopupVisible)
        {
            HidePopup();
            return;
        }

        if ((clickedAtUtc ?? DateTime.UtcNow) - _lastDismissedAtUtc < RecentDismissWindow)
            return;

        ShowNearAnchor();
    }

    public void ShowNearAnchor()
    {
        EnsureShellPage();

        // Reset navigation on each open, matching the old flyout behavior.
        _shellPage?.ResetNavigation();

        // Position before showing so the window never flashes at a stale location.
        WindowPlacementService.PositionWindowNearAnchor(this, PopupWidth, PopupHeight);
        _baseX = AppWindow.Position.X;
        _baseY = AppWindow.Position.Y;

        _hideTimer.Stop();
        _isShowing = true;
        _isPopupVisible = true;

        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        if (dpi == 0)
            dpi = 96;
        PlayShowAnimation(dpi);

        this.Show();
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);

        // The hosted page stays loaded while the popup is hidden, so being subscribed is
        // not the same as being able to show a dialog. Tell the error service when there
        // is actually a window for one to appear on.
        PlaybackErrorService.Instance.SetHostWindowVisible(true);

        // Once the window is back up, undo any deactivated (dimmed) title bar
        // visuals left over from the light-dismiss that hid it.
        DispatcherQueue.TryEnqueue(() => _shellPage?.RefreshTitleBarActivation());
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        _isShowing = false;

        if (!_isPopupVisible)
            return;

        _isPopupVisible = false;
        _lastDismissedAtUtc = DateTime.UtcNow;
        PlaybackErrorService.Instance.SetHostWindowVisible(false);
        PlayHideAnimation();
    }

    private void PlayShowAnimation(uint dpi)
    {
        _isHiding = false;
        _durationMs = ShowDurationMs;
        _fromOffset = ScaleForDpi(ShowSlideDistance, dpi);
        _toOffset = 0;
        _fromOpacity = 0;
        _toOpacity = 1;

        // Deterministic starting state before every show, so replays are clean
        // regardless of where a previous hide animation left off.
        ApplyFrame(_fromOffset, 0);

        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void PlayHideAnimation()
    {
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        if (dpi == 0)
            dpi = 96;

        // Start from wherever the popup currently is: HidePopup can land while a
        // show animation is still running, and snapping to the resting state
        // first would read as a visible jump.
        _isHiding = true;
        _durationMs = HideDurationMs;
        _fromOffset = _currentOffset;
        _toOffset = ScaleForDpi(HideSlideDistance, dpi);
        _fromOpacity = _currentOpacity;
        _toOpacity = 0;

        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        double progress = _animationClock.Elapsed.TotalMilliseconds / _durationMs;
        bool finished = progress >= 1;
        if (finished)
            progress = 1;

        // Cubic ease on the slide (out on the way in, in on the way out) with a
        // linear fade, matching SongChangePopupWindow's feel.
        double eased = _isHiding
            ? progress * progress * progress
            : 1 - Math.Pow(1 - progress, 3);

        int offset = _fromOffset + (int)Math.Round((_toOffset - _fromOffset) * eased);
        double opacity = _fromOpacity + ((_toOpacity - _fromOpacity) * progress);

        ApplyFrame(offset, opacity);

        if (!finished)
            return;

        _animationTimer.Stop();
        _animationClock.Reset();

        if (_isHiding)
            this.Hide();
    }

    private void ApplyFrame(int offset, double opacity)
    {
        if (_hwnd == 0)
            return;

        _currentOffset = offset;
        _currentOpacity = opacity;

        SetAlpha(opacity);
        AppWindow.Move(new PointInt32(_baseX, _baseY + offset));
    }

    private void SetAlpha(double opacity)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        COLORREF cOLORREF = new(0); // Color is ignored when using LWA_ALPHA
        _ = PInvoke.SetLayeredWindowAttributes(_hwnd, cOLORREF, alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    private static int ScaleForDpi(int logical, uint dpi) => (int)Math.Round(logical * dpi / 96.0);

    private void EnsureShellPage()
    {
        // One ShellPage kept alive across show/hide: reopening is instant and the
        // XamlRoot stays valid for ContentDialogs opened from hosted pages.
        if (_shellPage is null)
        {
            _shellPage = new ShellPage();
            ShellHost.Content = _shellPage;
        }
    }

    private void ConfigurePresenter()
    {
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = true;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        unsafe
        {
            uint cornerPreference = DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &cornerPreference, sizeof(int));
        }

        // WS_EX_LAYERED enables the per-window alpha the entrance/exit fade drives.
        int exStyle = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        _ = PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
    }

    private void ConfigureToolWindow()
    {
        // No taskbar button, no Alt-Tab entry.
        HWND hwnd = (HWND)WindowNative.GetWindowHandle(this);
        int exStyle = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // The presenter's SetBorderAndTitleBar(false, false) can leave the
        // caption/resize-frame styles behind, which DWM renders as a visible
        // frame around the popup. Strip them and the 1px DWM border directly.
        int style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style & ~(WS_CAPTION | WS_THICKFRAME));

        unsafe
        {
            uint cornerPreference = DWMWCP_ROUND;
            _ = PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &cornerPreference, sizeof(int));
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isShowing = false;
            _hideTimer.Stop();
            return;
        }

        if (_isShowing || !_isPopupVisible)
            return;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideIfFocusLeftWindow()
    {
        _hideTimer.Stop();

        if (!_isPopupVisible)
            return;

        HWND foreground = PInvoke.GetForegroundWindow();
        if (ShouldRemainVisible(foreground))
            return;

        HidePopup();
    }

    private bool ShouldRemainVisible(HWND foregroundWindow)
    {
        if (foregroundWindow == 0)
            return false;

        nint windowHandle = WindowNative.GetWindowHandle(this);
        if (foregroundWindow == windowHandle)
            return true;

        if (PInvoke.GetAncestor(foregroundWindow, GET_ANCESTOR_FLAGS.GA_ROOT) == windowHandle)
            return true;

        // WinUI MenuFlyouts/ComboBox dropdowns open in separate popup HWNDs;
        // classic context menus use the #32768 class. Neither should dismiss us.
        return IsTransientPopupWindow(foregroundWindow);
    }

    private static bool IsTransientPopupWindow(HWND hwnd)
    {
        System.Span<Char> className = [];
        if (PInvoke.GetClassName(hwnd, className) == 0)
            return false;

        string name = className.ToString();
        return string.Equals(name, "#32768", StringComparison.Ordinal)
            || string.Equals(name, "Xaml_WindowedPopupClass", StringComparison.Ordinal);
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HidePopup();
    }
}
