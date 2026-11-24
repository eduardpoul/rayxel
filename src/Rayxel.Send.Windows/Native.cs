using System.Runtime.InteropServices;

namespace Rayxel.Send.Windows;

/// <summary>The few Win32 calls the sender makes that neither .NET nor Vortice wraps.</summary>
internal static unsafe class Native
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly IntPtr PerMonitorAwareV2 = -4;

    /// <summary>
    /// Makes the calling thread see every display in its own physical pixels. Desktop
    /// duplication wants it - IDXGIOutput5::DuplicateOutput1 refuses a thread that is not
    /// per-monitor aware - and the pointer's place is then counted in the same pixels as the
    /// picture. Per thread, so the program around the sender is left as it is.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    public const uint InputMouse = 0, InputKeyboard = 1;

    public const uint KeyExtended = 0x0001, KeyUp = 0x0002, KeyScanCode = 0x0008;

    public const uint MouseMove = 0x0001;
    public const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    public const uint MouseRightDown = 0x0008, MouseRightUp = 0x0010;
    public const uint MouseMiddleDown = 0x0020, MouseMiddleUp = 0x0040;
    public const uint MouseXDown = 0x0080, MouseXUp = 0x0100;
    public const uint MouseWheel = 0x0800, MouseHWheel = 0x1000;
    public const uint XButton1 = 1, XButton2 = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    /// <summary>INPUT as it is on x64: the type, padding to eight bytes, then the largest member of the union.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input* inputs, int size);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("kernel32.dll")]
    public static extern int ProcessIdToSessionId(uint processId, out uint sessionId);
}
