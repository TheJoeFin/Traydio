using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using Trdo.Models;
using Trdo.Services;
using Trdo.ViewModels;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace Trdo.Controls;

/// <summary>
/// Transient, non-activating overlay window that briefly announces a song
/// change near the taskbar. A single instance is reused for the lifetime of
/// the app (see App.xaml.cs): repeated calls to <see cref="ShowSongChange"/>
/// update the content and restart the auto-hide timer, replaying the show
/// animation cleanly even if a previous show/hide animation is still in
/// flight.
/// </summary>
/// <remarks>
/// The window itself is the visible pill — it is sized to its content and
/// painted by <see cref="DesktopAcrylicBackdrop"/>, with DWM supplying the
/// rounded corners and drop shadow. That is why the animation here is driven
/// by a frame timer over the window's position and layered alpha rather than
/// by XAML storyboards: a system backdrop sits behind the XAML content and is
/// unaffected by <see cref="UIElement.Opacity"/>, so fading the XAML tree
/// would leave the acrylic slab visible. Per-window alpha is the only way to
/// fade the surface as a whole.
/// </remarks>
public sealed partial class SongChangePopupWindow : Window
{
    private const int WindowWidth = 440;
    private const int MinWindowHeight = 64;

    /// <summary>
    /// Logical pixels added to the measured content height so layout rounding
    /// cannot arrange the content a physical pixel taller than the pill it sits
    /// in. See the remarks on <see cref="MeasureContentHeight"/>.
    /// </summary>
    private const int LayoutRoundingHeadroom = 1;

    // Gap between the pill and the top of the taskbar. Previously this came
    // from the content's own bottom margin; now that the window is the pill,
    // it belongs to placement.
    private const int TaskbarGap = 12;

    private const int ShowSlideDistance = 24;
    private const int HideSlideDistance = 12;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(15);
    private const double ShowDurationMs = 250;
    private const double HideDurationMs = 180;

    private readonly DispatcherQueueTimer _autoHideTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();

    /// <summary>
    /// Delay presets offered on the popup's own menu. Chosen to cover the usual range of
    /// encoder lead time in one click; the station editor's slider reaches the rest of
    /// the range, up to a minute.
    /// </summary>
    private static readonly double[] DelayPresetsSeconds = [0, 2, 5, 10, 15];

    private nint _hwnd;
    private bool _isVisible;
    private bool _isConfigured;
    private bool _isMenuOpen;

    // Animation state. _baseY is the pill's resting Y; the slide animates an
    // offset below it. All in physical pixels, scaled to the target monitor.
    private bool _isHiding;
    private int _baseX;
    private int _baseY;
    private int _fromOffset;
    private int _toOffset;
    private double _fromOpacity;
    private double _toOpacity;
    private double _durationMs = ShowDurationMs;

    // Where the last rendered frame left the pill, so an animation that
    // interrupts another can start from the current state instead of snapping.
    private int _currentOffset;
    private double _currentOpacity;

    public SongChangePopupWindow()
    {
        InitializeComponent();

        // Paints the pill itself: real desktop acrylic, sampling whatever is
        // behind the window. The XAML content must stay transparent for this
        // to show through.
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _autoHideTimer = DispatcherQueue.CreateTimer();
        _autoHideTimer.IsRepeating = false;
        _autoHideTimer.Tick += AutoHideTimer_Tick;

        _animationTimer = DispatcherQueue.CreateTimer();
        _animationTimer.Interval = FrameInterval;
        _animationTimer.IsRepeating = true;
        _animationTimer.Tick += AnimationTimer_Tick;

        Closed += OnWindowClosed;
    }

    /// <summary>
    /// Updates the displayed song text and (re)shows the popup near the
    /// taskbar, restarting the auto-hide timer. Safe to call repeatedly for
    /// consecutive song changes: reuses the same window and replays the show
    /// animation even if a hide animation is currently in flight.
    /// </summary>
    public void ShowSongChange(string displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
            return;

        EnsureConfigured();

        SongText.Text = displayText;
        AutomationProperties.SetName(RootGrid, $"Now playing: {displayText}");

        // Same art PlayingPage/NowPlayingPage show - track art if the source has one, else the
        // station's own favicon/cover - and the same per-source-kind glyph behind it while that
        // loads or when there is none, so a local album or white noise station reads the same
        // way here as everywhere else "now playing" is shown.
        AlbumArtImage.Source = PlayerViewModel.Shared.CurrentAlbumArtImageSource;
        TrackIcon.Glyph = PlayerViewModel.Shared.CurrentAlbumArtPlaceholderGlyph;
        TrackIcon.Visibility = PlayerViewModel.Shared.CurrentAlbumArtPlaceholderVisibility;

        // The window hugs its content, so the height has to be remeasured for
        // every song: a title that wraps to two lines needs a taller pill.
        int height = MeasureContentHeight();

        RectInt32 bounds = WindowPlacementService.GetBottomCenterPlacement(
            this, WindowWidth, height, TaskbarGap, out uint dpi);

        // Grow the window rect by its invisible frame, so the *client* area —
        // all the XAML content ever gets — ends up the size the content was
        // measured at. Anchoring the window's bottom-right corner keeps the
        // visible pill's bottom and centre exactly where placement put them:
        // the extra pixels are taken off the top and split across the sides.
        (int frameWidth, int frameHeight) = GetFrameThickness();
        bounds.X -= frameWidth / 2;
        bounds.Y -= frameHeight;
        bounds.Width += frameWidth;
        bounds.Height += frameHeight;

        _baseX = bounds.X;
        _baseY = bounds.Y;

        AppWindow.MoveAndResize(bounds);

        PlayShowAnimation(dpi);

        StartAutoHideTimer();
    }

    /// <summary>
    /// (Re)starts the auto-hide countdown, reading the dwell time each time rather than
    /// caching it, so a change in Settings takes effect on the very next song.
    /// </summary>
    private void StartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Interval = TimeSpan.FromSeconds(SettingsService.SongChangePopupDwellSeconds);
        _autoHideTimer.Start();
    }

    /// <summary>
    /// Hides the popup with the hide animation and stops the auto-hide timer.
    /// Safe to call when already hidden.
    /// </summary>
    public void HidePopup()
    {
        _autoHideTimer.Stop();

        if (!_isVisible)
            return;

        PlayHideAnimation();
    }

    /// <summary>
    /// Stops the auto-hide timer and any in-flight animation, and hides the
    /// native window immediately. Intended for the window's own Closed
    /// handler (invoked on the UI thread when the app shuts down and closes
    /// all tracked windows); never called from a finalizer.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Tick -= AutoHideTimer_Tick;
        _animationTimer.Stop();
        _animationTimer.Tick -= AnimationTimer_Tick;
        _animationClock.Reset();

        if (_hwnd != 0)
        {
            _ = PInvoke.ShowWindow((HWND)_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    private void AutoHideTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        // Never yank the pill out from under an open menu.
        if (_isMenuOpen)
            return;

        HidePopup();
    }

    /// <summary>
    /// A left click dismisses the popup. The pill is hit-testable so it can offer the
    /// delay menu, so it will occasionally intercept a click meant for something behind
    /// it; dismissing is both the useful interpretation and the fastest way to get the
    /// pill out of the way.
    /// </summary>
    private void RootGrid_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        HidePopup();
    }

    /// <summary>
    /// Right-clicking offers the delay controls for the station that is playing. The
    /// delay is a per-station property in practice, and this is the moment the user
    /// can see it is wrong — so it is the natural place to correct it.
    /// </summary>
    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;

        MenuFlyout flyout = BuildDelayMenu();

        // Hold the pill open while the menu is up, and take foreground so the menu
        // light-dismisses properly - WS_EX_NOACTIVATE otherwise leaves it stranded
        // when the user clicks away.
        _isMenuOpen = true;
        _autoHideTimer.Stop();
        _ = PInvoke.SetForegroundWindow((HWND)_hwnd);

        flyout.Closed += OnDelayMenuClosed;
        flyout.ShowAt(RootGrid, new FlyoutShowOptions
        {
            Position = e.GetPosition(RootGrid),
            Placement = FlyoutPlacementMode.Top
        });
    }

    private void OnDelayMenuClosed(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
            flyout.Closed -= OnDelayMenuClosed;

        _isMenuOpen = false;

        // Give the user a moment to read the result of what they just picked rather
        // than snapping shut the instant the menu closes. Not when a hide is already
        // running: "Turn off song popups" dismisses from inside the menu, and _isVisible
        // stays true until that animation finishes.
        if (_isVisible && !_isHiding)
            StartAutoHideTimer();
    }

    private MenuFlyout BuildDelayMenu()
    {
        MenuFlyout flyout = new();

        RadioStation? station = PlayerViewModel.Shared.SelectedStation;
        double globalDelay = SettingsService.TrackInfoDelaySeconds;
        double? stationDelay = station?.SongPopupDelaySeconds;
        double effective = SongChangeAnnouncementPolicy.ResolveDelaySeconds(stationDelay, globalDelay);

        string header = station is null
            ? "Track info delay"
            : $"Track info delay for {station.Name}";

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = header,
            IsEnabled = false
        });
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (station is not null)
        {
            foreach (double preset in DelayPresetsSeconds)
            {
                double value = preset;
                ToggleMenuFlyoutItem item = new()
                {
                    Text = SongChangeAnnouncementPolicy.DescribeDelay(value),
                    IsChecked = stationDelay is not null && Math.Abs(stationDelay.Value - value) < 0.05
                };
                item.Click += (_, _) => ApplyStationDelay(station, value);
                flyout.Items.Add(item);
            }

            ToggleMenuFlyoutItem followApp = new()
            {
                Text = $"Use app setting ({SongChangeAnnouncementPolicy.DescribeDelay(globalDelay)})",
                IsChecked = stationDelay is null
            };
            followApp.Click += (_, _) => ApplyStationDelay(station, null);

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(followApp);
        }
        else
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = $"Currently {SongChangeAnnouncementPolicy.DescribeDelay(effective)} (no station selected)",
                IsEnabled = false
            });
        }

        MenuFlyoutItem turnOff = new() { Text = "Turn off song popups" };
        turnOff.Click += (_, _) =>
        {
            SettingsService.IsSongChangePopupEnabled = false;
            HidePopup();
        };

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(turnOff);

        return flyout;
    }

    /// <summary>
    /// Writes the chosen delay onto the station and persists it. Uses the flush-only
    /// save: <c>SaveStations</c> also reinitializes the stream, which would interrupt
    /// playback just for a popup timing tweak.
    /// </summary>
    private static void ApplyStationDelay(RadioStation station, double? seconds)
    {
        station.SongPopupDelaySeconds = seconds;
        PlayerViewModel.Shared.FlushStationsSave();
    }

    /// <summary>
    /// Measures the content against the pill's fixed width to get the height it
    /// needs. Width is deliberately fixed so the pill does not jump around
    /// between songs; only the height reacts, to accommodate wrapped titles.
    /// </summary>
    /// <remarks>
    /// A DIP of headroom is added on top of the measured height. Layout
    /// rounding snaps each arranged element to whole physical pixels, so at
    /// fractional scales (125%, 150%) the arranged content can end up a pixel
    /// past its own <c>DesiredSize</c> — and because everything in the pill is
    /// centre-aligned, a shortfall of even one pixel is split into a visible
    /// shave off the top *and* the bottom of the text. The headroom is well
    /// under a pixel per edge, and in the common single-line case it changes
    /// nothing at all: <see cref="MinWindowHeight"/> is still the binding
    /// constraint there.
    /// </remarks>
    private int MeasureContentHeight()
    {
        RootGrid.Measure(new Windows.Foundation.Size(WindowWidth, double.PositiveInfinity));
        double measured = RootGrid.DesiredSize.Height;

        // DesiredSize is 0 until the tree has been realized at least once;
        // EnsureConfigured activates the window to force that, but fall back
        // rather than collapsing the window to nothing if it ever does not.
        if (double.IsNaN(measured) || measured <= 0)
            return MinWindowHeight;

        return Math.Max(MinWindowHeight, (int)Math.Ceiling(measured) + LayoutRoundingHeadroom);
    }

    /// <summary>
    /// Physical-pixel difference between this window's outer rect and its
    /// client rect — the invisible DWM resize border that sits outside the
    /// visible pill. <c>AppWindow.MoveAndResize</c> sizes the outer rect, but
    /// the XAML content is laid out in the client rect, so a window sized
    /// straight from the measured content comes up short by this much.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shortfall does not simply crop the pill's edge: XAML layout-clips an
    /// element that is arranged smaller than it asked for, so the text is cut
    /// at RootGrid's padding box while the padding itself still renders in
    /// full. That is why it reads as a glyph with its descender sliced off
    /// rather than as an obviously too-small window.
    /// </para>
    /// <para>
    /// Measured rather than derived. <c>AppWindow.ResizeClient</c> — and the
    /// <c>AdjustWindowRectEx</c> underneath it — works from the window's
    /// *styles*, which still carry a caption that this presenter only hides.
    /// It therefore over-corrects by a caption's height and inflates the pill
    /// with roughly 32 DIP of dead space above and below the text.
    /// </para>
    /// </remarks>
    private (int Width, int Height) GetFrameThickness()
    {
        if (_hwnd == 0
            || !PInvoke.GetWindowRect((HWND)_hwnd, out RECT window)
            || !PInvoke.GetClientRect((HWND)_hwnd, out RECT client))
        {
            return (0, 0);
        }

        int width = (window.right - window.left) - (client.right - client.left);
        int height = (window.bottom - window.top) - (client.bottom - client.top);

        return (Math.Max(0, width), Math.Max(0, height));
    }

    private void EnsureConfigured()
    {
        if (_isConfigured)
            return;

        _isConfigured = true;

        _hwnd = WindowNative.GetWindowHandle(this);

        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;

        // Border kept, title bar dropped. The border is what DWM frames, and
        // the frame is what carries the rounded corners and the drop shadow —
        // stripping WS_CAPTION/WS_THICKFRAME here (as the transparent-window
        // version of this popup did) would take both with it.
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // No taskbar button, no Alt-Tab entry, and never takes activation/focus.
        // WS_EX_LAYERED enables the per-window alpha the fade animation drives.
        //
        // WS_EX_TRANSPARENT is deliberately NOT set: the pill has to receive mouse
        // input to offer click-to-dismiss and the right-click delay menu. The cost is
        // that for the couple of seconds it is on screen it sits over whatever is
        // beneath it — which is why a left click dismisses it, turning an intercepted
        // click into the action the user most likely wanted. Once hidden the HWND is
        // SW_HIDE'd, so it intercepts nothing the rest of the time.
        int exStyle = (int)PInvoke.GetWindowLong((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        _ = PInvoke.SetWindowLong(
            (HWND)_hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            exStyle | (int)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_LAYERED));

        DWM_WINDOW_CORNER_PREFERENCE cornerPreference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        unsafe
        {
            _ = PInvoke.DwmSetWindowAttribute(
                (HWND)_hwnd,
                DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                &cornerPreference,
                (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }

        // WinUI only creates and renders the XAML content once the window has
        // been activated; SW_SHOWNA alone leaves it blank, and DesiredSize
        // stays 0 so the window cannot be sized to its content. Activating
        // here is safe because WS_EX_NOACTIVATE is already applied (no focus
        // theft) and alpha is pinned to 0, so nothing flashes on screen.
        SetAlpha(0);
        Activate();
        _ = PInvoke.ShowWindow((HWND)_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
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

        _isVisible = true;

        // Show without activating/stealing focus, then animate in.
        _ = PInvoke.ShowWindow((HWND)_hwnd, SHOW_WINDOW_CMD.SW_SHOWNA);

        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void PlayHideAnimation()
    {
        uint dpi = PInvoke.GetDpiForWindow((HWND)_hwnd);
        if (dpi == 0)
            dpi = 96;

        // Start from wherever the pill currently is: HidePopup can land while a
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
        // linear fade, matching the feel of the storyboards this replaced.
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
        {
            _isVisible = false;

            if (_hwnd != 0)
            {
                _ = PInvoke.ShowWindow((HWND)_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            }
        }
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
        _ = PInvoke.SetLayeredWindowAttributes((HWND)_hwnd, new COLORREF(0), alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    private static int ScaleForDpi(int logical, uint dpi) => (int)Math.Round(logical * dpi / 96.0);
}
