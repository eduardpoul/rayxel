using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rayxel.Diagnostics;

namespace Rayxel.Capture;

/// <summary>
/// The key combination that hands the keyboard and mouse to the Deck and takes them back.
/// <para>
/// Ctrl+Alt+G unless told otherwise. It has to be something no game wants and nothing on
/// this machine already answers to, and the second is not a formality: a combination that
/// another program has registered as its hotkey never arrives here at all, because
/// Windows gives the key press to that program and to nobody else. Alt+R and Ctrl+Alt+R
/// both turned out to be taken on the machine this was written on. Scroll Lock does the
/// same job on keyboards that have one, whatever is set here.
/// </para>
/// </summary>
/// <param name="Key">The Linux key code of the key that completes the combination.</param>
/// <param name="VirtualKey">The same key as Windows names it, for asking who else has claimed it.</param>
internal readonly record struct CaptureKey(bool Ctrl, bool Alt, bool Shift, int Key, int VirtualKey, string Name)
{
    public static readonly CaptureKey Default =
        new(Ctrl: true, Alt: true, Shift: false, Key: 34, VirtualKey: 'G', Name: "Ctrl+Alt+G");

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    /// <summary>
    /// True when another program has this combination registered as a system-wide hotkey.
    /// Found out by trying to register it and letting go at once: Windows allows one owner,
    /// so a refusal means there is one already.
    /// </summary>
    public bool TakenElsewhere()
    {
        const int Probe = 0x5258, ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        uint modifiers = (Alt ? 1u : 0) | (Ctrl ? 2u : 0) | (Shift ? 4u : 0);

        if (RegisterHotKey(IntPtr.Zero, Probe, modifiers, (uint)VirtualKey))
        {
            UnregisterHotKey(IntPtr.Zero, Probe);
            return false;
        }
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ERROR_HOTKEY_ALREADY_REGISTERED;
    }

    // Keys by where they sit, in the kernel's numbering: the three letter rows of a
    // keyboard, left to right. A combination names a physical key, not a letter, so it is
    // the same key whichever layout is active.
    private const string TopRow = "qwertyuiop", HomeRow = "asdfghjkl", BottomRow = "zxcvbnm";

    /// <summary>
    /// Reads a combination written as <c>ctrl+alt+r</c>: any of Ctrl, Alt and Shift, then one
    /// letter, digit or function key. A letter or digit needs at least one modifier, or that
    /// key could never be typed on the Deck again.
    /// </summary>
    public static bool TryParse(string text, out CaptureKey key)
    {
        key = Default;
        bool ctrl = false, alt = false, shift = false;
        int code = 0, virtualKey = 0;
        string label = "";
        bool functionKey = false;

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = part.ToLowerInvariant();
            if (p is "ctrl" or "control") ctrl = true;
            else if (p == "alt") alt = true;
            else if (p == "shift") shift = true;
            else if (code != 0) return false; // two keys
            else if (p.Length == 1 && TopRow.IndexOf(p[0]) is >= 0 and var top) (code, label) = (16 + top, p.ToUpperInvariant());
            else if (p.Length == 1 && HomeRow.IndexOf(p[0]) is >= 0 and var home) (code, label) = (30 + home, p.ToUpperInvariant());
            else if (p.Length == 1 && BottomRow.IndexOf(p[0]) is >= 0 and var bottom) (code, label) = (44 + bottom, p.ToUpperInvariant());
            else if (p.Length == 1 && p[0] is >= '1' and <= '9') (code, label) = (2 + (p[0] - '1'), p);
            else if (p == "0") (code, label) = (11, p);
            else if (p.Length >= 2 && p[0] == 'f' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 12)
                (code, label, functionKey, virtualKey) = (f <= 10 ? 58 + f : 76 + f, p.ToUpperInvariant(), true, 0x6F + f);
            else return false;
        }

        bool modified = ctrl || alt || shift;
        if (code == 0 || (!modified && !functionKey)) return false;

        string name = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + label;
        // Letters and digits are their own character codes to Windows; the function keys
        // were given theirs above.
        if (virtualKey == 0) virtualKey = label[0];
        key = new CaptureKey(ctrl, alt, shift, code, virtualKey, name);
        return true;
    }
}

/// <summary>
/// Sends this machine's keyboard and mouse to the sender, to be reproduced there as a
/// keyboard and a mouse the Deck's games can see.
/// <para>
/// Unlike a controller, these cannot simply be shared: the same keys work this app and
/// the same mouse brings up its bar. So they go to the Deck only while <em>captured</em>,
/// a state the user switches with a key combination (see <see cref="CaptureKey"/>) and
/// that ends by itself when the window loses the focus. While it lasts, the
/// window ignores them entirely; see the window procedure.
/// </para>
/// <para>
/// Read through Raw Input on a thread of its own, with a window nobody sees. The app's
/// main thread looks at its messages once per displayed frame, which is fine for a hotkey
/// and would add most of a frame to every mouse movement; here each one is on the wire
/// within the millisecond it arrived in. Raw Input also gives the mouse's own counts,
/// before Windows has applied the pointer speed and acceleration a game does not want.
/// </para>
/// <para>
/// What is sent is state, as for the controller: every key that is down, and running
/// totals of the mouse's movement. A lost datagram is put right by the next.
/// </para>
/// </summary>
internal sealed class DeskForwarder : IDisposable
{
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_INPUT = 0x00FF, WM_TIMER = 0x0113;
    private const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    private const int RawHeaderSize = 24;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // Linux key codes for the keys that mean something here.
    private const int KeyScrollLock = 70, KeyPause = 119;
    private const int LeftCtrl = 29, RightCtrl = 97, LeftAlt = 56, RightAlt = 100, LeftShift = 42, RightShift = 54;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern unsafe uint GetRawInputData(IntPtr input, uint command, void* data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>The combination that switches capture on and off. Set once, at start-up.</summary>
    public static CaptureKey Key { get; set; } = CaptureKey.Default;

    private readonly NetLink _link;
    private readonly uint _session;
    private readonly Func<bool> _focused;
    private readonly string _className;
    private readonly WindowProcedure _procedure; // kept: Windows holds only the raw pointer

    private Thread? _thread;
    private volatile IntPtr _window;
    private volatile bool _running, _captured;

    // All of this belongs to the input thread.
    private readonly byte[] _physical = new byte[32]; // every key actually held, captured or not
    private readonly byte[] _down = new byte[32];     // what the Deck has been told
    private readonly byte[] _packet = new byte[NetWire.KeysSize];
    private byte _buttons;
    private int _x, _y, _wheel, _hwheel;
    private uint _keysSequence, _mouseSequence;
    private int _releaseRepeats;
    private bool _warnedAbsolute;

    public DeskForwarder(NetLink link, uint session, Func<bool> focused)
    {
        _link = link;
        _session = session;
        _focused = focused;
        _className = $"RayxelInput{session:x8}";
        _procedure = WindowProc;
    }

    /// <summary>True while the keyboard and mouse belong to the Deck and not to this window.</summary>
    public bool Captured => _captured;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "keyboard and mouse", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        IntPtr instance = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = instance,
            ClassName = _className
        };
        if (RegisterClassExW(ref windowClass) == 0)
        {
            Log.Warn("keyboard and mouse: the input window could not be registered; they are not forwarded");
            return;
        }

        try
        {
            // A message-only window: it exists to be sent input and is never shown.
            IntPtr window = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero)
            {
                Log.Warn("keyboard and mouse: the input window could not be created; they are not forwarded");
                return;
            }
            _window = window;

            // Generic desktop page: usage 6 is a keyboard, 2 a mouse. INPUTSINK delivers
            // their input here whichever window has the focus, which is what lets the
            // capture key work at all; whether to act on it is decided per event.
            RawInputDevice[] devices =
            [
                new() { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = window },
                new() { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = window },
            ];
            if (!RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawInputDevice>()))
                Log.Warn("keyboard and mouse: Windows refused raw input; they are not forwarded");
            else
                Log.Info($"keyboard and mouse: {Key.Name} hands them to the sender and takes them back");

            if (Key.TakenElsewhere())
                Log.Warn($"keyboard and mouse: {Key.Name} is already another program's hotkey on this machine, so pressing it " +
                         "never reaches Rayxel. Start with --capture-key= and a different combination, such as ctrl+alt+k");

            SetTimer(window, 1, 20, IntPtr.Zero);
            if (!_running) PostMessageW(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
                DispatchMessageW(ref message);
        }
        finally
        {
            _captured = false;
            _window = IntPtr.Zero;
            UnregisterClassW(_className, instance);
        }
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Nothing may escape: this is called from native code, where an exception does not
        // unwind, it ends the process.
        try
        {
            switch (message)
            {
                case WM_INPUT:
                    OnRawInput(lParam);
                    break; // and on to the default handler, which frees the input

                case WM_TIMER:
                    OnTick();
                    return IntPtr.Zero;

                case WM_CLOSE:
                    KillTimer(window, 1);
                    DestroyWindow(window);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error("keyboard and mouse: input handler failed", ex);
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private unsafe void OnRawInput(IntPtr input)
    {
        // A keyboard report is 40 bytes with its header and a mouse report 48.
        byte* buffer = stackalloc byte[64];
        uint size = 64;
        if (GetRawInputData(input, RID_INPUT, buffer, ref size, RawHeaderSize) == uint.MaxValue) return;

        var data = new ReadOnlySpan<byte>(buffer, (int)size);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var body = data[RawHeaderSize..];

        if (type == RIM_TYPEKEYBOARD && body.Length >= 8)
        {
            OnKey(make: BinaryPrimitives.ReadUInt16LittleEndian(body),
                  flags: BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
                  virtualKey: BinaryPrimitives.ReadUInt16LittleEndian(body[6..]));
        }
        else if (type == RIM_TYPEMOUSE && body.Length >= 20)
        {
            OnMouse(flags: BinaryPrimitives.ReadUInt16LittleEndian(body),
                    buttonFlags: BinaryPrimitives.ReadUInt16LittleEndian(body[4..]),
                    buttonData: BinaryPrimitives.ReadInt16LittleEndian(body[6..]),
                    dx: BinaryPrimitives.ReadInt32LittleEndian(body[12..]),
                    dy: BinaryPrimitives.ReadInt32LittleEndian(body[16..]));
        }
    }

    private void OnKey(ushort make, ushort flags, ushort virtualKey)
    {
        // 0xFF marks the filler half of a two-part scan code; the real half comes separately.
        if (virtualKey == 0xFF) return;

        bool down = (flags & 1) == 0;
        int key = KeyCode(make, extended: (flags & 2) != 0, pause: (flags & 4) != 0 || virtualKey == 0x13, virtualKey);
        if (key == 0) return;

        bool wasDown = Has(_physical, key);
        Set(_physical, key, down);

        // The key that completes the combination is this app's own and never reaches the
        // Deck. The modifiers before it could not be known to be part of one, so when the
        // combination ends a capture the Deck has already seen them go down; it sees them
        // released with everything else a moment later.
        var capture = Key;
        bool chord = key == capture.Key
                     && Held(LeftCtrl, RightCtrl) == capture.Ctrl
                     && Held(LeftAlt, RightAlt) == capture.Alt
                     && Held(LeftShift, RightShift) == capture.Shift;
        if (key == KeyScrollLock || chord)
        {
            if (down && !wasDown) Toggle();
            return;
        }

        if (!_captured || Has(_down, key) == down) return;
        Set(_down, key, down);
        SendKeys();
    }

    private void OnMouse(ushort flags, ushort buttonFlags, short buttonData, int dx, int dy)
    {
        if (!_captured) return;

        // Bit 0 set means positions rather than movements: a tablet, or a remote desktop
        // session. There is nothing relative in those to send.
        if ((flags & 1) != 0)
        {
            if (!_warnedAbsolute) Log.Warn("keyboard and mouse: this pointer reports positions, not movement; its motion is not forwarded");
            _warnedAbsolute = true;
            dx = dy = 0;
        }

        _x += dx;
        _y += dy;

        // Each button has a "went down" bit and a "went up" bit, two apart.
        for (int button = 0; button < 5; button++)
        {
            if ((buttonFlags & (1 << (button * 2))) != 0) _buttons |= (byte)(1 << button);
            if ((buttonFlags & (2 << (button * 2))) != 0) _buttons &= (byte)~(1 << button);
        }
        if ((buttonFlags & 0x0400) != 0) _wheel += buttonData;  // already in 120ths of a notch
        if ((buttonFlags & 0x0800) != 0) _hwheel += buttonData;

        SendMouse();
    }

    private void OnTick()
    {
        // A window that is no longer in front has no business typing on the Deck, and
        // Alt+Tab would otherwise leave Alt held there for good.
        if (_captured && !_focused())
        {
            Log.Info("keyboard and mouse: the window lost the focus, taken back from the sender");
            Release();
        }

        if (!_captured && _releaseRepeats == 0) return;
        if (!_captured) _releaseRepeats--;

        // The same state again, fifty times a second: it repairs anything lost, and its
        // absence is how the sender learns the link has gone and lets go of everything.
        SendKeys();
        SendMouse();
    }

    private void Toggle()
    {
        if (_captured)
        {
            Log.Info("keyboard and mouse: taken back from the sender");
            Release();
            return;
        }
        if (!_focused()) return;

        // Starts clean: a key already held when capture begins is not a press on the Deck.
        Array.Clear(_down);
        _buttons = 0;
        _releaseRepeats = 0;
        _captured = true;
        Log.Info($"keyboard and mouse: now going to the sender - {Key.Name} takes them back");
        SendKeys();
        SendMouse();
    }

    /// <summary>Everything up, said several times over so that it is not the one datagram that gets lost.</summary>
    private void Release()
    {
        _captured = false;
        Array.Clear(_down);
        _buttons = 0;
        _releaseRepeats = 3;
        SendKeys();
        SendMouse();
    }

    private void SendKeys() => Send(NetWire.WriteKeys(_packet, _session, ++_keysSequence, _down, _link.Version));

    private void SendMouse() =>
        Send(NetWire.WriteMouse(_packet, _session, ++_mouseSequence, _buttons, _x, _y, _wheel, _hwheel, _link.Version));

    private void Send(int length)
    {
        try { _link.Send(_packet.AsSpan(0, length)); }
        catch (ObjectDisposedException)
        {
            // The connection is going away; whoever owns it is dealing with that.
        }
    }

    private bool Held(int left, int right) => Has(_physical, left) || Has(_physical, right);

    private static bool Has(byte[] keys, int key) => (keys[key >> 3] & (1 << (key & 7))) != 0;

    private static void Set(byte[] keys, int key, bool down)
    {
        if (down) keys[key >> 3] |= (byte)(1 << (key & 7));
        else keys[key >> 3] &= (byte)~(1 << (key & 7));
    }

    /// <summary>
    /// The Linux key code for a key, from the scan code the keyboard sent. For the main
    /// block the two are the same number — Linux took its codes from the PC keyboard — so
    /// this names the physical key, whatever layout either machine is set to. Only the keys
    /// that arrive with a prefix, added to the keyboard later, need looking up.
    /// </summary>
    private static int KeyCode(ushort make, bool extended, bool pause, ushort virtualKey)
    {
        // Pause is sent as a prefixed Ctrl followed by Num Lock, and is recognised whole.
        if (pause) return KeyPause;

        // Some software keyboards send no scan code at all. MAPVK_VK_TO_VSC_EX recovers it,
        // with the prefix, when there is one, in the high byte.
        if (make == 0 && virtualKey != 0)
        {
            uint scan = MapVirtualKeyW(virtualKey, 4);
            make = (ushort)(scan & 0xFF);
            extended = (scan & 0xFF00) == 0xE000;
        }

        if (!extended) return make is >= 1 and <= 0x58 ? make : 0;

        return make switch
        {
            0x1C => 96,  // keypad Enter
            0x1D => RightCtrl,
            0x35 => 98,  // keypad /
            0x37 => 99,  // Print Screen
            0x38 => RightAlt,
            0x47 => 102, // Home
            0x48 => 103, // Up
            0x49 => 104, // Page Up
            0x4B => 105, // Left
            0x4D => 106, // Right
            0x4F => 107, // End
            0x50 => 108, // Down
            0x51 => 109, // Page Down
            0x52 => 110, // Insert
            0x53 => 111, // Delete
            0x5B => 125, // left Windows key
            0x5C => 126, // right Windows key
            0x5D => 127, // Menu
            // Anything else with a prefix is a media or power key, and those stay here:
            // the volume keys should turn down this machine, which is where the sound is.
            _ => 0
        };
    }

    public void Dispose()
    {
        _running = false;
        IntPtr window = _window;
        if (window != IntPtr.Zero) PostMessageW(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
        _thread = null;
    }
}
