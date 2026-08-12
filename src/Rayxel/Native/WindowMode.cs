using Rayxel.Diagnostics;

namespace Rayxel.Native;

/// <summary>The three window modes the design offers, in the order it lists them.</summary>
public enum WindowKind
{
    /// <summary>«в окне» — an ordinary resizable window with a frame.</summary>
    Windowed,

    /// <summary>
    /// «на весь экран» — frameless, covering the monitor, and held above everything else.
    /// Topmost is the point: a notification popping over the window takes the swapchain off
    /// its overlay plane, which costs a whole frame.
    /// </summary>
    Fullscreen,

    /// <summary>«в окне на весь экран» — frameless and covering the monitor, normal z-order.</summary>
    BorderlessFullscreen
}

/// <summary>
/// Switches the window between the three modes.
/// <para>
/// This is the one control that can change the composition mode. A windowed swapchain is
/// composited by the desktop manager, which costs a whole frame — 8.3 ms at 120 fps, more
/// than RTX VSR and HDR put together. Covering the monitor with a plain popup window is
/// what lets DXGI hand the swapchain to a hardware overlay plane instead; measured on a
/// 3440×1440 display, that is the difference between <c>present: Composed</c> and
/// <c>present: Overlay</c>.
/// </para>
/// <para>
/// Deliberately not DXGI exclusive fullscreen: it changes the display mode, takes the
/// screen from everything else and is unpleasant to alt-tab out of. Borderless reaches the
/// same overlay plane without any of it.
/// </para>
/// </summary>
public sealed class WindowMode
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    private readonly IntPtr _hwnd;
    private Win32.RECT _windowedRect;
    private IntPtr _windowedStyle;
    private bool _savedWindowed;

    public WindowKind Kind { get; private set; } = WindowKind.Windowed;

    public bool Borderless => Kind != WindowKind.Windowed;

    public WindowMode(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>F11 walks the three in the order the design lists them.</summary>
    public void Cycle() => Set(Kind switch
    {
        WindowKind.Windowed => WindowKind.BorderlessFullscreen,
        WindowKind.BorderlessFullscreen => WindowKind.Fullscreen,
        _ => WindowKind.Windowed
    });

    public void Set(WindowKind kind)
    {
        if (kind == Kind) return;

        if (kind == WindowKind.Windowed) LeaveBorderless();
        else EnterBorderless(topmost: kind == WindowKind.Fullscreen);

        Kind = kind;
    }

    private void EnterBorderless(bool topmost)
    {
        // Remember exactly what to put back, and only the first time — going from one
        // borderless mode to the other must not record the borderless geometry as "the
        // window", or there is nothing to restore.
        if (!_savedWindowed)
        {
            _windowedStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE);
            if (!Win32.GetWindowRect(_hwnd, out _windowedRect))
            {
                Log.Warn("cannot read the window rect; borderless would not be reversible");
                return;
            }
            _savedWindowed = true;
        }

        var monitor = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>()
        };
        if (!Win32.GetMonitorInfoW(monitor, ref info))
        {
            Log.Warn("cannot read monitor bounds; staying windowed");
            return;
        }

        // WS_POPUP with nothing else: no frame, no caption, no resize border. The whole
        // point is that DWM has nothing of ours left to compose.
        long style = (long)_windowedStyle & ~(long)Win32.WS_OVERLAPPEDWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE,
            (IntPtr)(style | (long)Win32.WS_POPUP | (long)Win32.WS_VISIBLE));

        var r = info.Monitor;
        Win32.SetWindowPos(_hwnd, topmost ? HwndTopmost : HwndNoTopmost,
            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        Win32.SetForegroundWindow(_hwnd);

        Log.Info($"{(topmost ? "fullscreen" : "borderless fullscreen")} on {r.Right - r.Left}x{r.Bottom - r.Top}" +
                 " - watch 'present:' for the overlay plane");
    }

    private void LeaveBorderless()
    {
        if (!_savedWindowed) return;

        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE, _windowedStyle);
        var r = _windowedRect;
        Win32.SetWindowPos(_hwnd, HwndNoTopmost, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        _savedWindowed = false;
        Log.Info("windowed again");
    }
}
