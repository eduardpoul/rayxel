using Rayxel.Link;
using Rayxel.Link.Input;

namespace Rayxel.Send.Windows;

/// <summary>
/// The viewer's keyboard and mouse, typed and moved on this desktop through SendInput.
/// <para>
/// Keys arrive as Linux key codes, which name the physical key rather than what is printed on
/// it, and they go out here as the scan codes that key sends. So the key under the viewer's
/// finger is the same key on this machine, and the letter it makes is the one this machine's
/// own layout gives it - as with a USB keyboard plugged in here. The mouse goes out as
/// relative movement, buttons and wheels, much as a real mouse's reports do; Windows applies
/// its pointer speed to the movement as it would to theirs, and programs that read raw input
/// see the counts as sent.
/// </para>
/// <para>
/// What SendInput cannot reach, nothing here can: the secure desktop (the UAC prompt, the
/// lock screen, Ctrl+Alt+Del) and the windows of programs running at a higher integrity than
/// the sender - an elevated program has to be met by an elevated sender. Input that Windows
/// turns away is said once in the log, and again when it is taken once more.
/// </para>
/// </summary>
public sealed unsafe class SendInputDesk : IDeskSink
{
    /// <summary>Put on every event, so that a program that cares can tell the sender's input from a person's: "RXL!".</summary>
    private const nuint Signature = 0x52584C21;

    private const int KeyNumLock = 69, KeyPause = 119;
    private const ushort VkNumLock = 0x90, VkPause = 0x13;

    /// <summary>
    /// The set 1 scan code each Linux key code is sent as, with 0xE000 added for the keys
    /// that carry the E0 prefix; 0 for a key with no scan code here.
    /// </summary>
    private static readonly ushort[] ScanCodes = MakeScanCodes();

    private static readonly (byte Bit, uint Down, uint Up, uint Data)[] Buttons =
    [
        (Wire.MouseLeft, Native.MouseLeftDown, Native.MouseLeftUp, 0),
        (Wire.MouseRight, Native.MouseRightDown, Native.MouseRightUp, 0),
        (Wire.MouseMiddle, Native.MouseMiddleDown, Native.MouseMiddleUp, 0),
        (Wire.MouseBack, Native.MouseXDown, Native.MouseXUp, Native.XButton1),
        (Wire.MouseForward, Native.MouseXDown, Native.MouseXUp, Native.XButton2),
    ];

    private readonly object _gate = new();
    private readonly byte[] _down = new byte[32];
    private readonly byte[] _saidUnknown = new byte[32];
    private byte _buttons;
    private bool _havePosition;
    private int _x, _y, _wheel, _hwheel;
    private bool _refused, _disposed;

    private SendInputDesk()
    {
    }

    public static SendInputDesk? Open(out string why)
    {
        if (Native.ProcessIdToSessionId((uint)Environment.ProcessId, out uint session) != 0 && session == 0)
        {
            why = "this sender runs as a service, in session 0, which has no desktop to type on or move a pointer over";
            return null;
        }
        why = "";
        LinkLog.Info("keyboard: the viewer's keyboard and mouse go to this desktop through SendInput");
        return new SendInputDesk();
    }

    public bool Active
    {
        get
        {
            lock (_gate) return _buttons != 0 || _down.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        }
    }

    public void Keys(ReadOnlySpan<byte> down)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Span<Native.Input> inputs = stackalloc Native.Input[256];
            int count = 0;

            for (int key = 1; key < 256; key++)
            {
                int index = key >> 3;
                byte mask = (byte)(1 << (key & 7));
                bool now = index < down.Length && (down[index] & mask) != 0;
                bool before = (_down[index] & mask) != 0;
                if (now == before) continue;

                if (now) _down[index] |= mask;
                else _down[index] &= (byte)~mask;
                if (KeyEvent(key, now, out var input)) inputs[count++] = input;
            }
            Send(inputs[..count]);
        }
    }

    public void Mouse(byte buttons, int x, int y, int wheel, int hwheel)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Span<Native.Input> inputs = stackalloc Native.Input[8];
            int count = 0;

            // The first message only sets where the totals start. After it, the differences;
            // taken unsigned, so that a total that has wrapped round still gives the right step.
            int dx = 0, dy = 0, dw = 0, dh = 0;
            if (_havePosition)
            {
                dx = (int)((uint)x - (uint)_x);
                dy = (int)((uint)y - (uint)_y);
                dw = (int)((uint)wheel - (uint)_wheel);
                dh = (int)((uint)hwheel - (uint)_hwheel);
            }

            // Movement before buttons: a press that came with movement happened where the
            // movement ended.
            if (dx != 0 || dy != 0) inputs[count++] = MouseEvent(Native.MouseMove, dx, dy, 0);
            foreach (var (bit, press, release, data) in Buttons)
            {
                if (((buttons ^ _buttons) & bit) == 0) continue;
                inputs[count++] = MouseEvent((buttons & bit) != 0 ? press : release, 0, 0, data);
            }
            // Both wheels take 120ths of a notch, which is what arrives, fine scrolling and all.
            if (dw != 0) inputs[count++] = MouseEvent(Native.MouseWheel, 0, 0, (uint)dw);
            if (dh != 0) inputs[count++] = MouseEvent(Native.MouseHWheel, 0, 0, (uint)dh);

            _buttons = buttons;
            _havePosition = true;
            _x = x;
            _y = y;
            _wheel = wheel;
            _hwheel = hwheel;
            Send(inputs[..count]);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            Keys(stackalloc byte[32]);
            Mouse(0, _x, _y, _wheel, _hwheel);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Release();
            _disposed = true;
        }
    }

    private bool KeyEvent(int key, bool down, out Native.Input input)
    {
        input = default;
        input.Type = Native.InputKeyboard;
        input.Keyboard.ExtraInfo = Signature;
        uint up = down ? 0 : Native.KeyUp;

        // Num Lock and Pause share scan code 0x45, told apart by prefixes that SendInput has
        // no way to give. They go by virtual key instead, which cannot be mistaken.
        if (key == KeyNumLock)
        {
            input.Keyboard.Vk = VkNumLock;
            input.Keyboard.Scan = 0x45;
            input.Keyboard.Flags = Native.KeyExtended | up;
            return true;
        }
        if (key == KeyPause)
        {
            input.Keyboard.Vk = VkPause;
            input.Keyboard.Scan = 0x45;
            input.Keyboard.Flags = up;
            return true;
        }

        ushort code = ScanCodes[key];
        if (code == 0)
        {
            int index = key >> 3;
            byte mask = (byte)(1 << (key & 7));
            if (down && (_saidUnknown[index] & mask) == 0)
            {
                _saidUnknown[index] |= mask;
                LinkLog.Debug($"keyboard: Linux key {key} has no Windows scan code here and is not typed");
            }
            return false;
        }
        input.Keyboard.Scan = (ushort)(code & 0xFF);
        input.Keyboard.Flags = Native.KeyScanCode | ((code & 0xFF00) == 0xE000 ? Native.KeyExtended : 0) | up;
        return true;
    }

    private static Native.Input MouseEvent(uint flags, int dx, int dy, uint data)
    {
        var input = new Native.Input { Type = Native.InputMouse };
        input.Mouse.Dx = dx;
        input.Mouse.Dy = dy;
        input.Mouse.MouseData = data;
        input.Mouse.Flags = flags;
        input.Mouse.ExtraInfo = Signature;
        return input;
    }

    /// <summary>All of one message's events in one call, so that nothing else is injected in between.</summary>
    private void Send(Span<Native.Input> inputs)
    {
        if (inputs.IsEmpty) return;
        uint sent;
        fixed (Native.Input* events = inputs) sent = Native.SendInput((uint)inputs.Length, events, sizeof(Native.Input));

        if (sent != inputs.Length)
        {
            if (_refused) return;
            _refused = true;
            LinkLog.Warn("keyboard: Windows turned the viewer's input away - the UAC prompt, the lock screen or a " +
                         "program running as administrator has the focus");
        }
        else if (_refused)
        {
            _refused = false;
            LinkLog.Info("keyboard: Windows takes the viewer's input again");
        }
    }

    private static ushort[] MakeScanCodes()
    {
        var scan = new ushort[256];

        // The main block, the keypad and F1 to F12: Linux numbered these keys by the scan
        // codes of the PC keyboard, so the two are the same (and the viewer, reading scan
        // codes, sends them as such). Num Lock goes by virtual key, see KeyEvent.
        for (int key = 1; key <= 88; key++) scan[key] = (ushort)key;
        scan[KeyNumLock] = 0;

        // The keys that came later, with the E0 prefix.
        scan[96] = 0xE01C;  // keypad Enter
        scan[97] = 0xE01D;  // right Ctrl
        scan[98] = 0xE035;  // keypad /
        scan[99] = 0xE037;  // Print Screen
        scan[100] = 0xE038; // right Alt (AltGr)
        scan[102] = 0xE047; // Home
        scan[103] = 0xE048; // Up
        scan[104] = 0xE049; // Page Up
        scan[105] = 0xE04B; // Left
        scan[106] = 0xE04D; // Right
        scan[107] = 0xE04F; // End
        scan[108] = 0xE050; // Down
        scan[109] = 0xE051; // Page Down
        scan[110] = 0xE052; // Insert
        scan[111] = 0xE053; // Delete
        scan[125] = 0xE05B; // left Windows
        scan[126] = 0xE05C; // right Windows
        scan[127] = 0xE05D; // Menu

        // F13 to F24, which some keyboards and macro pads have.
        ReadOnlySpan<ushort> f13 = [0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76];
        for (int i = 0; i < f13.Length; i++) scan[183 + i] = f13[i];

        // Keys of Japanese and Brazilian keyboards, and the keypad's = and comma.
        scan[89] = 0x73;  // Ro
        scan[92] = 0x79;  // Henkan
        scan[93] = 0x70;  // Katakana/Hiragana
        scan[94] = 0x7B;  // Muhenkan
        scan[117] = 0x59; // keypad =
        scan[121] = 0x7E; // keypad , (Brazilian)
        scan[124] = 0x7D; // Yen

        // Media, browser and power keys: the scan codes Windows gives them, which its layouts
        // turn into the volume, playback and launch keys.
        scan[113] = 0xE020; // Mute
        scan[114] = 0xE02E; // Volume Down
        scan[115] = 0xE030; // Volume Up
        scan[116] = 0xE05E; // Power
        scan[128] = 0xE068; // Stop (browser)
        scan[140] = 0xE021; // Calculator
        scan[142] = 0xE05F; // Sleep
        scan[143] = 0xE063; // Wake
        scan[155] = 0xE06C; // Mail
        scan[156] = 0xE066; // Favourites
        scan[157] = 0xE06B; // My Computer
        scan[158] = 0xE06A; // Back (browser)
        scan[159] = 0xE069; // Forward (browser)
        scan[163] = 0xE019; // Next Track
        scan[164] = 0xE022; // Play/Pause
        scan[165] = 0xE010; // Previous Track
        scan[166] = 0xE024; // Stop (media)
        scan[172] = 0xE032; // Home (browser)
        scan[173] = 0xE067; // Refresh
        scan[217] = 0xE065; // Search
        scan[226] = 0xE06D; // Media Select
        return scan;
    }
}
