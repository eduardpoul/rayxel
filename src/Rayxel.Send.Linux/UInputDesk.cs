using Rayxel.Link;
using Rayxel.Link.Input;
using Rayxel.Send.Linux.UInput;
using static Rayxel.Send.Linux.UInput.UInputDevice;

namespace Rayxel.Send.Linux;

/// <summary>
/// A keyboard and a mouse that exist only in software, driven by the viewer's real ones.
/// <para>
/// Made through uinput like <see cref="UInputPad"/>, and for the same reason indistinguishable
/// from hardware: gamescope picks them up the way it would a keyboard plugged into the dock. They
/// are two devices rather than one because that is what real ones are, and what the libraries
/// that sort devices into kinds expect to find.
/// </para>
/// </summary>
public sealed class UInputDesk : IDeskSink
{
    // Key codes above this are not keys on a keyboard.
    private const int LastKey = 248;

    private const int DownBytes = 32;

    private static readonly (byte Bit, ushort Key)[] MouseButtons =
    [
        (Wire.MouseLeft, BtnLeft),
        (Wire.MouseRight, BtnRight),
        (Wire.MouseMiddle, BtnMiddle),
        (Wire.MouseBack, BtnSide),
        (Wire.MouseForward, BtnExtra),
    ];

    private static readonly ushort[] MouseAxes = [RelX, RelY, RelWheel, RelHWheel, RelWheelHiRes, RelHWheelHiRes];

    private readonly object _gate = new();
    private int _keyboard, _mouse;
    private readonly byte[] _down = new byte[DownBytes];
    private byte _buttons;
    private bool _havePosition;
    private int _x, _y, _wheel, _hwheel;
    private int _wheelRest, _hwheelRest; // part of a notch not yet reported as a whole one

    private UInputDesk(int keyboard, int mouse)
    {
        _keyboard = keyboard;
        _mouse = mouse;
    }

    /// <summary>Creates both devices. Null on failure, with the reason.</summary>
    public static UInputDesk? Open(out string why)
    {
        int keyboard = OpenKeyboard(out string error);
        int mouse = keyboard >= 0 ? OpenMouse(out error) : -1;
        if (mouse < 0)
        {
            why = $"the virtual keyboard and mouse could not be created: {error}";
            if (keyboard >= 0) Destroy(keyboard);
            return null;
        }
        why = "";
        return new UInputDesk(keyboard, mouse);
    }

    private static int OpenKeyboard(out string error)
    {
        int fd = UInputDevice.Open();
        if (fd < 0)
        {
            error = LastError();
            return -1;
        }

        bool made = SetBit(fd, UiSetEvBit, EvKey);
        for (int key = 1; key <= LastKey && made; key++) made = SetBit(fd, UiSetKeyBit, key);
        made = made && Create(fd, "Rayxel keyboard", 0x5258, 0x0001, 1);
        return Finish(fd, made, out error);
    }

    private static int OpenMouse(out string error)
    {
        int fd = UInputDevice.Open();
        if (fd < 0)
        {
            error = LastError();
            return -1;
        }

        bool made = SetBit(fd, UiSetEvBit, EvKey) && SetBit(fd, UiSetEvBit, EvRel);
        foreach (var (_, key) in MouseButtons) made = made && SetBit(fd, UiSetKeyBit, key);
        foreach (ushort axis in MouseAxes) made = made && SetBit(fd, UiSetRelBit, axis);
        made = made && Create(fd, "Rayxel mouse", 0x5258, 0x0002, 1);
        return Finish(fd, made, out error);
    }

    private static int Finish(int fd, bool made, out string error)
    {
        if (made)
        {
            error = "";
            return fd;
        }
        error = LastError();
        CloseHandle(fd);
        return -1;
    }

    /// <summary>Makes the keyboard match <paramref name="down"/>: bit n set means Linux key code n is held.</summary>
    public void Keys(ReadOnlySpan<byte> down)
    {
        lock (_gate)
        {
            if (_keyboard < 0) return;
            Span<byte> next = stackalloc byte[DownBytes];
            down[..Math.Min(down.Length, DownBytes)].CopyTo(next);
            bool changed = false;

            for (int key = 1; key <= LastKey; key++)
            {
                byte mask = (byte)(1 << (key & 7));
                bool now = (next[key >> 3] & mask) != 0, before = (_down[key >> 3] & mask) != 0;
                if (now == before) continue;
                Emit(_keyboard, EvKey, (ushort)key, now ? 1 : 0);
                changed = true;
            }
            if (changed) Emit(_keyboard, EvSyn, SynReport, 0);
            next.CopyTo(_down);
        }
    }

    /// <summary>
    /// Makes the mouse match. The positions are running totals kept by the viewer, not
    /// movements, so what is applied is the difference from the totals seen last time - and a
    /// datagram that went missing takes no movement with it. The first call only records where
    /// the totals stand.
    /// </summary>
    public void Mouse(byte buttons, int x, int y, int wheel, int hwheel)
    {
        lock (_gate)
        {
            if (_mouse < 0) return;
            bool changed = false;

            foreach (var (bit, key) in MouseButtons)
            {
                if (((_buttons ^ buttons) & bit) == 0) continue;
                Emit(_mouse, EvKey, key, (buttons & bit) != 0 ? 1 : 0);
                changed = true;
            }
            _buttons = buttons;

            if (_havePosition)
            {
                // Two's complement wrap-around, so that a total that has wrapped round still gives the right step.
                int dx = unchecked(x - _x), dy = unchecked(y - _y);
                if (dx != 0) Emit(_mouse, EvRel, RelX, dx);
                if (dy != 0) Emit(_mouse, EvRel, RelY, dy);
                changed |= dx != 0 || dy != 0;
                changed |= Scroll(unchecked(wheel - _wheel), ref _wheelRest, RelWheelHiRes, RelWheel);
                changed |= Scroll(unchecked(hwheel - _hwheel), ref _hwheelRest, RelHWheelHiRes, RelHWheel);
            }
            _havePosition = true;
            _x = x;
            _y = y;
            _wheel = wheel;
            _hwheel = hwheel;

            if (changed) Emit(_mouse, EvSyn, SynReport, 0);
        }
    }

    /// <summary>True while any key or mouse button is held.</summary>
    public bool Active
    {
        get
        {
            lock (_gate) return _buttons != 0 || _down.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        }
    }

    /// <summary>Lets go of every key and button.</summary>
    public void Release()
    {
        lock (_gate)
        {
            Keys([]);

            // Before the first mouse message there is no button to let go of, and no totals to
            // keep: taking zeros for them would make the first real message a jump across the
            // screen.
            if (_havePosition) Mouse(0, _x, _y, _wheel, _hwheel);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_keyboard < 0) return;
            Release();
            Destroy(_keyboard);
            Destroy(_mouse);
            _keyboard = _mouse = -1;
        }
    }

    /// <summary>
    /// The wheel arrives in 120ths of a notch. Programs that understand fine scrolling get exactly
    /// that; the rest are given a whole notch each time the 120ths add up to one.
    /// </summary>
    private bool Scroll(int delta, ref int rest, ushort fine, ushort coarse)
    {
        if (delta == 0) return false;
        Emit(_mouse, EvRel, fine, delta);

        rest = unchecked(rest + delta);
        int notches = rest / 120;
        if (notches != 0)
        {
            Emit(_mouse, EvRel, coarse, notches);
            rest -= notches * 120;
        }
        return true;
    }
}
