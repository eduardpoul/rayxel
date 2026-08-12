using ImGuiNET;
using Rayxel.Native;

namespace Rayxel.Render;

/// <summary>
/// Translates window messages into ImGui input events.
/// <para>
/// Kept apart from <see cref="ImGuiLayer"/> because it runs on a different thread of
/// control: this is called from inside the window procedure, while rendering happens on
/// the frame loop. ImGui's event queue is what makes that safe — events are recorded here
/// and consumed at NewFrame, so nothing touches draw state from the message pump.
/// </para>
/// </summary>
public static class ImGuiInput
{
    /// <summary>
    /// Feeds one message to ImGui. Returns true when ImGui has taken it and the app should
    /// not also act on it — a click on a settings panel must not fall through to the video.
    /// </summary>
    public static bool Handle(ImGuiLayer layer, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!layer.Available) return false;

        var io = ImGui.GetIO();

        switch ((int)msg)
        {
            case Win32.WM_MOUSEMOVE:
                io.AddMousePosEvent(Win32.LoWord(lParam), Win32.HiWord(lParam));
                return false; // never swallowed: the app tracks hover for its own reasons

            case Win32.WM_MOUSELEAVE:
                io.AddMousePosEvent(float.MinValue, float.MinValue);
                return false;

            case Win32.WM_LBUTTONDOWN:
            case Win32.WM_LBUTTONDBLCLK:
                return Button(io, hwnd, 0, true);
            case Win32.WM_LBUTTONUP:
                return Button(io, hwnd, 0, false);

            case Win32.WM_RBUTTONDOWN:
            case Win32.WM_RBUTTONDBLCLK:
                return Button(io, hwnd, 1, true);
            case Win32.WM_RBUTTONUP:
                return Button(io, hwnd, 1, false);

            case Win32.WM_MBUTTONDOWN:
            case Win32.WM_MBUTTONDBLCLK:
                return Button(io, hwnd, 2, true);
            case Win32.WM_MBUTTONUP:
                return Button(io, hwnd, 2, false);

            case Win32.WM_MOUSEWHEEL:
                io.AddMouseWheelEvent(0f, WheelDelta(wParam));
                return io.WantCaptureMouse;

            case Win32.WM_MOUSEHWHEEL:
                io.AddMouseWheelEvent(WheelDelta(wParam), 0f);
                return io.WantCaptureMouse;

            case Win32.WM_KEYDOWN:
            case Win32.WM_SYSKEYDOWN:
            case Win32.WM_KEYUP:
            case Win32.WM_SYSKEYUP:
            {
                bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                UpdateModifiers(io);
                if (ToImGuiKey((int)wParam) is { } key) io.AddKeyEvent(key, down);
                // Swallowed only while a text field is being typed into - not merely
                // because a panel has focus. This is an overlay used while playing a game;
                // a hotkey that stops working because a window is open is a bug.
                return io.WantTextInput;
            }

            case Win32.WM_CHAR:
                io.AddInputCharacter((uint)wParam);
                return io.WantTextInput;

            case Win32.WM_KILLFOCUS:
                io.AddFocusEvent(false);
                return false;

            case Win32.WM_SETFOCUS:
                io.AddFocusEvent(true);
                return false;
        }

        return false;
    }

    private static bool Button(ImGuiIOPtr io, IntPtr hwnd, int index, bool down)
    {
        // Capture keeps a drag alive when the pointer leaves the window, which is what
        // makes dragging a slider past the edge behave instead of sticking.
        if (down) Win32.SetCapture(hwnd); else Win32.ReleaseCapture();
        io.AddMouseButtonEvent(index, down);
        return io.WantCaptureMouse;
    }

    private static float WheelDelta(IntPtr wParam) =>
        (short)((ulong)wParam >> 16) / 120f;

    private static void UpdateModifiers(ImGuiIOPtr io)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, Down(0x11));
        io.AddKeyEvent(ImGuiKey.ModShift, Down(0x10));
        io.AddKeyEvent(ImGuiKey.ModAlt, Down(0x12));
        io.AddKeyEvent(ImGuiKey.ModSuper, Down(0x5B) || Down(0x5C));
        static bool Down(int vk) => (Win32.GetKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>
    /// Virtual-key to ImGui key. Only the keys a UI actually needs — the rest of the
    /// keyboard reaches the app's own handler untouched, which is the point.
    /// </summary>
    private static ImGuiKey? ToImGuiKey(int vk) => vk switch
    {
        0x08 => ImGuiKey.Backspace,
        0x09 => ImGuiKey.Tab,
        0x0D => ImGuiKey.Enter,
        0x1B => ImGuiKey.Escape,
        0x20 => ImGuiKey.Space,
        0x21 => ImGuiKey.PageUp,
        0x22 => ImGuiKey.PageDown,
        0x23 => ImGuiKey.End,
        0x24 => ImGuiKey.Home,
        0x25 => ImGuiKey.LeftArrow,
        0x26 => ImGuiKey.UpArrow,
        0x27 => ImGuiKey.RightArrow,
        0x28 => ImGuiKey.DownArrow,
        0x2D => ImGuiKey.Insert,
        0x2E => ImGuiKey.Delete,
        0x10 => ImGuiKey.LeftShift,
        0x11 => ImGuiKey.LeftCtrl,
        0x12 => ImGuiKey.LeftAlt,
        >= 0x30 and <= 0x39 => ImGuiKey._0 + (vk - 0x30),
        >= 0x41 and <= 0x5A => ImGuiKey.A + (vk - 0x41),
        >= 0x70 and <= 0x7B => ImGuiKey.F1 + (vk - 0x70),
        _ => null
    };

    /// <summary>
    /// Applies the cursor ImGui asked for. Called from WM_SETCURSOR, which is the only
    /// message where setting a cursor sticks — Windows resets it on every mouse move.
    /// </summary>
    public static bool ApplyCursor(ImGuiLayer layer)
    {
        if (!layer.Available) return false;

        var cursor = ImGui.GetMouseCursor();
        if (cursor == ImGuiMouseCursor.None) { Win32.SetCursor(IntPtr.Zero); return true; }

        int id = cursor switch
        {
            ImGuiMouseCursor.TextInput => Win32.IDC_IBEAM,
            ImGuiMouseCursor.ResizeAll => Win32.IDC_SIZEALL,
            ImGuiMouseCursor.ResizeNS => Win32.IDC_SIZENS,
            ImGuiMouseCursor.ResizeEW => Win32.IDC_SIZEWE,
            ImGuiMouseCursor.ResizeNESW => Win32.IDC_SIZENESW,
            ImGuiMouseCursor.ResizeNWSE => Win32.IDC_SIZENWSE,
            ImGuiMouseCursor.Hand => Win32.IDC_HAND,
            ImGuiMouseCursor.NotAllowed => Win32.IDC_NO,
            _ => (int)Win32.IDC_ARROW
        };

        Win32.SetCursor(Win32.LoadCursorW(IntPtr.Zero, id));
        return true;
    }
}
