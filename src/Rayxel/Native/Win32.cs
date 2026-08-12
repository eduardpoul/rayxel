using System.Runtime.InteropServices;

namespace Rayxel.Native;

/// <summary>
/// Minimal Win32 window. Deliberately not WinForms/WPF: the presenter needs a plain
/// HWND with no framework message pumping or composition of its own, otherwise the
/// swapchain never reaches independent flip and we silently pay a frame to DWM.
/// </summary>
public static unsafe class Win32
{
    public const int WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_SIZE = 0x0005;
    public const int WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;
    public const int WM_COMMAND = 0x0111, WM_MOUSEMOVE = 0x0200;
    public const int VK_ESCAPE = 0x1B, VK_F1 = 0x70;

    // The rest of the input set, needed once ImGui is the one reading them.
    public const int WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_CHAR = 0x0102;
    public const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206;
    public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MBUTTONDBLCLK = 0x0209;
    public const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_MOUSELEAVE = 0x02A3;
    public const int WM_SETFOCUS = 0x0007, WM_KILLFOCUS = 0x0008;
    public const int WM_SETCURSOR = 0x0020;
    public const int WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0, ICON_BIG = 1;

    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr hInstance, int cursorId);

    // IDC_ARROW is declared further down as an IntPtr for CreateAppWindow's class registration.
    public const int IDC_IBEAM = 32513, IDC_SIZEALL = 32646,
                     IDC_SIZENS = 32645, IDC_SIZEWE = 32644, IDC_SIZENESW = 32643,
                     IDC_SIZENWSE = 32642, IDC_HAND = 32649, IDC_NO = 32648;

    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint TPM_LEFTALIGN = 0x0000, TPM_TOPALIGN = 0x0000, TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    public const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_POPUP = 0x0010,
                      MF_CHECKED = 0x0008, MF_UNCHECKED = 0x0000, MF_DISABLED = 0x0002, MF_GRAYED = 0x0001;

    public const int PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public POINT pt;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public IntPtr lpszMenuName, lpszClassName, hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint flags);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    // Takes both the classic DIB form and the PNG-compressed one, which is what every
    // image in rayxel.ico is. See AppIcon, which picks the image and calls this.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(IntPtr bits, uint size, bool isIcon,
        uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string? text);
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT Monitor, Work;
        public uint Flags;
    }

    public const int GWL_STYLE = -16;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_NOACTIVATE = 0x0010;

    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(IntPtr name);

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    /// <summary>
    /// Gives a windowed build somewhere to print. Attaches to the console it was launched
    /// from when there is one, and opens a new one otherwise, then re-points the CLR's
    /// cached stdout at it — without that, writes go to the handle captured at start-up
    /// and vanish.
    /// </summary>
    public static void AttachParentConsole()
    {
        const int AttachParentProcess = -1;
        if (!AttachConsole(AttachParentProcess) && !AllocConsole()) return;

        try
        {
            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        }
        catch (IOException) { /* no usable handle; the log file still has everything */ }
    }

    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);

    private static bool _dpiAware;

    /// <summary>
    /// Per-monitor DPI awareness, once — Windows only accepts it once per process, and only
    /// before the first window. Anything that measures in pixels has to come after it:
    /// GetSystemMetrics answers in virtualised units on the other side of this call, which
    /// is how a 150% display ends up with a 32px icon where a 48px one was available.
    /// </summary>
    public static void EnsureDpiAwareness()
    {
        if (_dpiAware) return;
        _dpiAware = true;
        try { SetProcessDpiAwareness(2); } catch { /* pre-8.1 shells */ }
    }

    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    public const int SM_CXICON = 11, SM_CYICON = 12, SM_CXSMICON = 49, SM_CYSMICON = 50;
    public static readonly IntPtr IDC_ARROW = 32512;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    /// <summary>
    /// Creates a normal resizable window, centred on the primary display.
    /// Note that a windowed swapchain is composed by DWM rather than reaching an
    /// overlay plane, which costs a frame — the HUD reports which one we got.
    /// </summary>
    public static IntPtr CreateAppWindow(string title, WndProc proc, int clientWidth, int clientHeight,
                                         IntPtr icon = default, IntPtr smallIcon = default)
    {
        EnsureDpiAwareness();

        var instance = GetModuleHandleW(IntPtr.Zero);
        const string className = "UpscalerDemoWindow";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)sizeof(WNDCLASSEX),
            style = 0x0020, // CS_OWNDC
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hIcon = icon,
            hIconSm = smallIcon,
            hbrBackground = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(className)
        };
        RegisterClassExW(ref wc);

        // Grow the outer size so the *client* area matches what the caller asked for.
        var rect = new RECT { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
        AdjustWindowRect(ref rect, WS_OVERLAPPEDWINDOW, false);
        int outerW = rect.Right - rect.Left, outerH = rect.Bottom - rect.Top;

        int x = Math.Max(0, (GetSystemMetrics(SM_CXSCREEN) - outerW) / 2);
        int y = Math.Max(0, (GetSystemMetrics(SM_CYSCREEN) - outerH) / 2);

        var hwnd = CreateWindowExW(0, className, title, WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            x, y, outerW, outerH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        // Also set per window, not only on the class: the class is registered once, and a
        // second call here would find it already there and silently keep the first icon.
        SetWindowIcon(hwnd, icon, smallIcon);
        SetForegroundWindow(hwnd);
        return hwnd;
    }

    public const int WM_SYSCOMMAND = 0x0112;

    /// <summary>
    /// Our own command in the window's system menu — the one a right-click on the title bar
    /// opens. Ids there must be below 0xF000, which is where the system's own start, and a
    /// multiple of 16: Windows uses the low four bits of wParam internally, so an id that
    /// sets them comes back changed.
    /// </summary>
    public const int SC_APP_SETTINGS = 0x1010;

    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);
    // CharSet.Unicode is not decoration: without it the runtime marshals the string as ANSI
    // and hands an 8-bit pointer to a W function, which renders as garbage — and worse for
    // "Параметры" than for "Settings", so it would have looked fine in the wrong language.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool InsertMenuW(IntPtr menu, uint position, uint flags,
                                          UIntPtr id, string? text);

    public const uint MF_BYPOSITION = 0x0400;

    /// <summary>
    /// Puts an item at the top of the window's system menu. Inserted rather than appended:
    /// the system menu ends with Close, and burying ours under it reads as an afterthought.
    /// <para>
    /// Only reachable while the window has a frame — the borderless mode has no title bar to
    /// right-click, which is exactly when the panel's own hotkey is the way in.
    /// </para>
    /// </summary>
    public static void AddSystemMenuItem(IntPtr hwnd, int id, string text)
    {
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero) return;
        InsertMenuW(menu, 0, MF_BYPOSITION | MF_STRING, (UIntPtr)id, text);
        InsertMenuW(menu, 1, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
    }

    /// <summary>Title bar, taskbar button and Alt-Tab. Either handle may be zero.</summary>
    public static void SetWindowIcon(IntPtr hwnd, IntPtr icon, IntPtr smallIcon)
    {
        if (icon != IntPtr.Zero) SendMessageW(hwnd, WM_SETICON, ICON_BIG, icon);
        if (smallIcon != IntPtr.Zero) SendMessageW(hwnd, WM_SETICON, ICON_SMALL, smallIcon);
    }

    [DllImport("user32.dll")] public static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);

    /// <summary>Creates a borderless window covering the primary display.</summary>
    public static IntPtr CreateFullscreenWindow(string title, WndProc proc, out int width, out int height,
                                                IntPtr icon = default, IntPtr smallIcon = default)
    {
        // Per-monitor DPI awareness, so the swapchain matches physical pixels.
        EnsureDpiAwareness();

        var instance = GetModuleHandleW(IntPtr.Zero);
        const string className = "UpscalerDemoWindow";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)sizeof(WNDCLASSEX),
            style = 0x0020, // CS_OWNDC
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hIcon = icon,
            hIconSm = smallIcon,
            hbrBackground = IntPtr.Zero, // we clear via D3D; letting GDI paint causes flashes
            lpszClassName = Marshal.StringToHGlobalUni(className)
        };
        RegisterClassExW(ref wc);

        width = GetSystemMetrics(SM_CXSCREEN);
        height = GetSystemMetrics(SM_CYSCREEN);

        var hwnd = CreateWindowExW(WS_EX_APPWINDOW, className, title, WS_POPUP | WS_VISIBLE,
            0, 0, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        SetWindowIcon(hwnd, icon, smallIcon);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
        return hwnd;
    }

    public static int LoWord(IntPtr v) => (int)((long)v & 0xFFFF);
    public static int HiWord(IntPtr v) => (int)(((long)v >> 16) & 0xFFFF);

    /// <summary>Drains pending messages. Returns false once WM_QUIT arrives.</summary>
    public static bool PumpMessages()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == 0x0012) return false; // WM_QUIT
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return true;
    }
}
