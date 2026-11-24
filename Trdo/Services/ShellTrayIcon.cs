using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Trdo.Services;

/// <summary>
/// How a notification-area icon is identified to the shell. Used to build the
/// <c>NOTIFYICONIDENTIFIER</c> for <c>Shell_NotifyIconGetRect</c>.
/// </summary>
/// <param name="WindowHandle">The hidden window that owns the icon.</param>
/// <param name="Id">The <c>uID</c>; only meaningful when <paramref name="Guid"/> is null.</param>
/// <param name="Guid">The registered GUID, or null when the icon fell back to <c>uID</c> identity.</param>
internal readonly record struct ShellTrayIconIdentity(nint WindowHandle, uint Id, Guid? Guid);

/// <summary>
/// A notification-area icon registered with <c>NIF_GUID</c>, replacing WinUIEx's <c>TrayIcon</c>.
/// </summary>
/// <remarks>
/// <para>
/// WinUIEx identifies its icon by <c>hWnd</c> + <c>uID</c> only, which Windows 11 keys on the
/// executable path for the saved "promoted to the taskbar" state. An MSIX update changes that
/// path, so the icon lost its promotion on every update. Registering with a GUID
/// (see <see cref="TrayIconIdentityPolicy"/>) keeps the stored entry across a path change.
/// </para>
/// <para>
/// Owns a hidden, invisible top-level window purely to receive the icon's callback message and the
/// <c>TaskbarCreated</c> broadcast (a message-only window would not get that broadcast). It must be
/// created and used on the UI thread, whose message loop delivers those messages.
/// </para>
/// <para>
/// If the GUID registration is refused (another process holds it, or a future Windows build
/// enforces the documented Authenticode rule), the icon falls back to plain <c>uID</c> identity so
/// the app still has a tray icon; it just won't keep its promoted state across an update. It never
/// issues a <c>NIM_DELETE</c> for a GUID it failed to add, since that would remove the icon of
/// whichever process actually owns it.
/// </para>
/// </remarks>
internal sealed unsafe class ShellTrayIcon : IDisposable
{
    private const string Component = "TrayIcon";
    private const string WindowClassName = "Trdo.ShellTrayIconHost";

    // Shell_NotifyIcon commands.
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;

    // NOTIFYICONDATA.uFlags.
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_GUID = 0x20;
    private const uint NIF_SHOWTIP = 0x80;

    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const uint FallbackId = 1;
    private const int MaxTooltipLength = 127; // szTip is 128 chars including the terminator

    // Events delivered in the low word of lParam under NOTIFYICON_VERSION_4.
    private const uint NIN_SELECT = 0x0400;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDBLCLK = 0x0206;

    private const uint WS_POPUP = 0x80000000;
    private const uint WM_NCCREATE = 0x0081;
    private const int GWLP_USERDATA = -21;
    private const int SM_CXSMICON = 49;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const nint IDI_APPLICATION = 32512;

    private static readonly uint WM_TASKBARCREATED = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    private static bool s_classRegistered;

    // Posted from the mouse hook to the host window so the hook itself returns immediately: a
    // low-level hook that dawdles delays every mouse event on the machine.
    private const uint WheelMessage = 0x8002; // WM_APP + 2
    private const int WH_MOUSE_LL = 14;
    private const nint WM_MOUSEWHEEL = 0x020A;

    // At most one icon owns the hook; the hook procedure is a static function pointer.
    private static ShellTrayIcon? s_hookOwner;

    private readonly Guid _guid;
    private readonly nint _hwnd;
    private nint _hook;
    private GCHandle _selfHandle;
    private nint _ownedIcon;
    private string _tooltip;
    private bool _useGuid = true;
    private bool _isVisible;
    private bool _added;
    private bool _disposed;

    public ShellTrayIcon(Guid guid, string iconPath, string tooltip)
    {
        _guid = guid;
        _tooltip = Truncate(tooltip);
        _hwnd = CreateHostWindow();
        _selfHandle = GCHandle.Alloc(this);
        NativeMethods.SetWindowLongPtrW(_hwnd, GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
        SetIcon(iconPath);
    }

    /// <summary>
    /// Creates the icon under the GUID appropriate for how this process is running (see
    /// <see cref="TrayIconIdentityPolicy"/>).
    /// </summary>
    public static ShellTrayIcon CreateForCurrentApp(string iconPath, string tooltip)
    {
        Guid guid = TrayIconIdentityPolicy.Choose(IsPackaged(), Environment.ProcessPath);
        return new ShellTrayIcon(guid, iconPath, tooltip);
    }

    private static bool IsPackaged()
    {
        const int APPMODEL_ERROR_NO_PACKAGE = 15700;
        uint length = 0;
        return NativeMethods.GetCurrentPackageFullName(&length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    public event EventHandler? Selected;
    public event EventHandler? ContextMenu;
    public event EventHandler? LeftDoubleClick;
    public event EventHandler? RightDoubleClick;

    /// <summary>
    /// The mouse wheel turned while the pointer was over the icon. The argument is the raw
    /// <c>WHEEL_DELTA</c> units (120 per notch; positive is away from the user), which finer
    /// wheels and touchpads deliver in smaller pieces.
    /// </summary>
    /// <remarks>Only raised while <see cref="IsWheelEnabled"/> is true.</remarks>
    public event EventHandler<int>? MouseWheel;

    /// <summary>
    /// Whether wheel turns over the icon are captured and raised as <see cref="MouseWheel"/>.
    /// </summary>
    /// <remarks>
    /// The shell never forwards the wheel to a notification icon, so this installs a low-level
    /// mouse hook and hit-tests the pointer against the icon's rectangle itself. A wheel turn
    /// over the icon is swallowed (so the taskbar underneath doesn't also react); every other
    /// input passes straight through. The hook exists only while this is true, because it
    /// sits in the path of all mouse input system-wide.
    /// </remarks>
    public bool IsWheelEnabled
    {
        get => _hook != 0;
        set
        {
            ThrowIfDisposed();
            if (value == (_hook != 0))
                return;

            if (value)
                InstallWheelHook();
            else
                RemoveWheelHook();
        }
    }

    /// <summary>
    /// How the shell currently knows this icon. Re-read it rather than caching: a failed GUID
    /// registration switches the icon to <c>uID</c> identity.
    /// </summary>
    public ShellTrayIconIdentity Identity => new(_hwnd, FallbackId, _useGuid ? _guid : null);

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            ThrowIfDisposed();
            if (_isVisible == value)
                return;

            _isVisible = value;
            if (value)
                AddToTray();
            else
                RemoveFromTray();
        }
    }

    /// <summary>
    /// The hover text. Like WinUIEx, the native tooltip is only touched when the value changes.
    /// </summary>
    public string Tooltip
    {
        get => _tooltip;
        set
        {
            ThrowIfDisposed();
            string tooltip = Truncate(value);
            if (_tooltip == tooltip)
                return;

            _tooltip = tooltip;
            if (_added)
                Modify(NIF_TIP | NIF_SHOWTIP, _ownedIcon);
        }
    }

    /// <summary>
    /// Loads an .ico file (relative paths resolve against the app's install folder) and shows it.
    /// </summary>
    /// <exception cref="ArgumentException">The file couldn't be loaded as an icon.</exception>
    public void SetIcon(string iconPath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(iconPath);

        string path = Path.IsPathRooted(iconPath) ? iconPath : Path.Combine(AppContext.BaseDirectory, iconPath);
        int size = Math.Max(NativeMethods.GetSystemMetrics(SM_CXSMICON), 16);

        nint icon;
        fixed (char* pathPtr = path)
        {
            icon = NativeMethods.LoadImageW(0, pathPtr, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        }

        if (icon == 0)
            throw new ArgumentException($"Failed to load icon from {iconPath}", nameof(iconPath));

        nint previous = _ownedIcon;
        _ownedIcon = icon;

        if (_added)
            Modify(NIF_ICON | NIF_TIP | NIF_SHOWTIP, icon);

        if (previous != 0)
            NativeMethods.DestroyIcon(previous);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        RemoveWheelHook();

        _disposed = true;
        _isVisible = false;
        RemoveFromTray();

        if (_hwnd != 0)
        {
            NativeMethods.SetWindowLongPtrW(_hwnd, GWLP_USERDATA, 0);
            NativeMethods.DestroyWindow(_hwnd);
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        if (_ownedIcon != 0)
        {
            NativeMethods.DestroyIcon(_ownedIcon);
            _ownedIcon = 0;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static string Truncate(string? tooltip)
    {
        tooltip ??= string.Empty;
        return tooltip.Length <= MaxTooltipLength ? tooltip : tooltip[..MaxTooltipLength];
    }

    // ---- registration with the shell ----

    private void AddToTray()
    {
        // After an Explorer restart our old icon is already gone; deleting it first (it is ours,
        // so this is safe) just guarantees the add below isn't refused as a duplicate.
        RemoveFromTray();

        nint icon = _ownedIcon != 0
            ? _ownedIcon
            : NativeMethods.LoadIconW(0, IDI_APPLICATION);

        _useGuid = true;
        if (TryAdd(icon))
            return;

        LogService.Warn(Component,
            $"NIM_ADD with GUID {_guid} was refused (held by another process, or a Windows build " +
            "enforcing the signing rule); falling back to uID identity. The icon will work but " +
            "won't keep its promoted taskbar placement across an update.");

        _useGuid = false;
        if (!TryAdd(icon))
            LogService.Error(Component, "NIM_ADD failed even without a GUID; the tray icon is unavailable.");
    }

    private bool TryAdd(nint icon)
    {
        NativeNotifyIconData data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP, icon);
        data.uCallbackMessage = CallbackMessage;

        if (NativeMethods.Shell_NotifyIconW(NIM_ADD, &data) == 0)
            return false;

        // The version isn't persisted by the shell; it must be set after every add.
        data.uVersion = NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NIM_SETVERSION, &data);
        _added = true;
        return true;
    }

    private void Modify(uint flags, nint icon)
    {
        NativeNotifyIconData data = CreateData(flags, icon);
        NativeMethods.Shell_NotifyIconW(NIM_MODIFY, &data);
    }

    private void RemoveFromTray()
    {
        // Only ever delete an icon this instance successfully added: with a GUID, a delete after a
        // failed add would remove the icon belonging to the process that holds the GUID.
        if (!_added)
            return;

        _added = false;
        NativeNotifyIconData data = CreateData(0, 0);
        NativeMethods.Shell_NotifyIconW(NIM_DELETE, &data);
    }

    /// <summary>
    /// Every call must identify the icon the same way it was added: a GUID if it was added with
    /// one, otherwise <c>uID</c>.
    /// </summary>
    private NativeNotifyIconData CreateData(uint flags, nint icon)
    {
        NativeNotifyIconData data = default;
        data.cbSize = (uint)sizeof(NativeNotifyIconData);
        data.hWnd = _hwnd;
        data.uFlags = flags;
        data.hIcon = icon;

        if (_useGuid)
        {
            data.uFlags |= NIF_GUID;
            data.guidItem = _guid;
        }
        else
        {
            data.uID = FallbackId;
        }

        string tooltip = _tooltip;
        for (int i = 0; i < tooltip.Length; i++)
            data.szTip[i] = tooltip[i];

        return data;
    }

    // ---- mouse wheel ----

    private void InstallWheelHook()
    {
        nint hook = NativeMethods.SetWindowsHookExW(
            WH_MOUSE_LL,
            (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&MouseHookProc,
            NativeMethods.GetModuleHandleW(null),
            0);

        if (hook == 0)
        {
            LogService.Warn(Component, $"Couldn't install the mouse wheel hook (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        _hook = hook;
        s_hookOwner = this;
    }

    private void RemoveWheelHook()
    {
        if (_hook == 0)
            return;

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = 0;

        if (ReferenceEquals(s_hookOwner, this))
            s_hookOwner = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseHookProc(int code, nint wParam, nint lParam)
    {
        try
        {
            // HC_ACTION is 0; anything negative must be passed on untouched.
            if (code >= 0 && wParam == WM_MOUSEWHEEL && s_hookOwner is ShellTrayIcon owner)
            {
                MSLLHOOKSTRUCT* info = (MSLLHOOKSTRUCT*)lParam;
                if (owner.TryCaptureWheel(info->x, info->y, (short)(info->mouseData >> 16)))
                    return 1; // swallow: the taskbar under the icon shouldn't see this turn
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an UnmanagedCallersOnly method would take the process down.
            LogService.Error(Component, "Mouse hook failed.", ex);
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private bool TryCaptureWheel(int x, int y, short delta)
    {
        if (!_added || delta == 0 || !IsPointOverIcon(x, y))
            return false;

        NativeMethods.PostMessageW(_hwnd, WheelMessage, delta, 0);
        return true;
    }

    private bool IsPointOverIcon(int x, int y)
    {
        NativeIconIdentifier id = default;
        id.cbSize = (uint)sizeof(NativeIconIdentifier);

        // Identify the icon exactly as it was added: by GUID alone, or by window and uID.
        if (_useGuid)
        {
            id.guidItem = _guid;
        }
        else
        {
            id.hWnd = _hwnd;
            id.uID = FallbackId;
        }

        NativeRect rect;
        if (NativeMethods.Shell_NotifyIconGetRect(&id, &rect) != 0)
            return false;

        return x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom;
    }

    // ---- hidden host window ----

    private static nint CreateHostWindow()
    {
        nint hInstance = NativeMethods.GetModuleHandleW(null);

        fixed (char* className = WindowClassName)
        {
            if (!s_classRegistered)
            {
                WNDCLASSEXW wc = default;
                wc.cbSize = (uint)sizeof(WNDCLASSEXW);
                wc.lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc;
                wc.hInstance = hInstance;
                wc.lpszClassName = className;
                NativeMethods.RegisterClassExW(&wc);
                s_classRegistered = true;
            }

            // A real top-level window (never shown) rather than a message-only one: message-only
            // windows don't receive the "TaskbarCreated" broadcast.
            nint hwnd = NativeMethods.CreateWindowExW(0, className, className, WS_POPUP, 0, 0, 0, 0, 0, 0, hInstance, 0);
            if (hwnd == 0)
                throw new InvalidOperationException($"Failed to create the tray icon window (error {Marshal.GetLastWin32Error()}).");

            return hwnd;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message != WM_NCCREATE)
            {
                nint raw = NativeMethods.GetWindowLongPtrW(hwnd, GWLP_USERDATA);
                if (raw != 0 && GCHandle.FromIntPtr(raw).Target is ShellTrayIcon icon)
                    icon.HandleMessage(message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an UnmanagedCallersOnly method would take the process down.
            LogService.Error(Component, "Tray icon message handler failed.", ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (_disposed)
            return;

        if (message == WM_TASKBARCREATED && WM_TASKBARCREATED != 0)
        {
            // Explorer restarted and dropped every icon: register again, trying the GUID first.
            if (_isVisible)
                AddToTray();
            return;
        }

        if (message == WheelMessage)
        {
            MouseWheel?.Invoke(this, (int)wParam);
            return;
        }

        if (message != CallbackMessage)
            return;

        switch ((uint)lParam & 0xFFFF)
        {
            case NIN_SELECT:
                Selected?.Invoke(this, EventArgs.Empty);
                break;
            case WM_CONTEXTMENU:
                ContextMenu?.Invoke(this, EventArgs.Empty);
                break;
            case WM_LBUTTONDBLCLK:
                LeftDoubleClick?.Invoke(this, EventArgs.Empty);
                break;
            case WM_RBUTTONDBLCLK:
                RightDoubleClick?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // ---- interop ----

    // Matches the NOTIFYICONDATAW layout in shellapi.h (Windows Vista+, includes guidItem).
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeNotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion; // union with uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    // NOTIFYICONIDENTIFIER, RECT and MSLLHOOKSTRUCT from shellapi.h / windef.h / winuser.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconIdentifier
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", ExactSpelling = true)]
        public static extern int Shell_NotifyIconW(uint dwMessage, NativeNotifyIconData* lpData);

        [DllImport("shell32.dll", ExactSpelling = true)]
        public static extern int Shell_NotifyIconGetRect(NativeIconIdentifier* identifier, NativeRect* iconLocation);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint dwThreadId);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern nint CreateWindowExW(
            uint dwExStyle, char* lpClassName, char* lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int DestroyWindow(nint hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW([MarshalAs(UnmanagedType.LPWStr)] string lpString);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint LoadImageW(nint hInst, char* name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern nint LoadIconW(nint hInstance, nint lpIconName);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int DestroyIcon(nint hIcon);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern nint GetModuleHandleW(char* lpModuleName);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern int GetCurrentPackageFullName(uint* packageFullNameLength, char* packageFullName);
    }
}
