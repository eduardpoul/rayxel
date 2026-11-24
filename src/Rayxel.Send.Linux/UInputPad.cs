using Rayxel.Link;
using Rayxel.Link.Input;
using Rayxel.Send.Linux.UInput;
using static Rayxel.Send.Linux.UInput.UInputDevice;

namespace Rayxel.Send.Linux;

/// <summary>
/// A gamepad that exists only in software, driven by the viewer's real one.
/// <para>
/// It is made through the kernel's uinput interface, the same way Steam makes the virtual
/// controller it hands to games, so nothing on the Deck can tell it from a pad plugged in: Steam
/// lists it, Steam Input remaps it, and a game sees it. It presents itself as an Xbox 360
/// controller because that is the one layout everything already knows.
/// </para>
/// </summary>
public sealed class UInputPad : IPadSink
{
    /// <summary>
    /// Wire bit to Linux key code, in the order the kernel's own Xbox driver declares them. That
    /// order is what game libraries count buttons by, so it is part of the disguise.
    /// </summary>
    private static readonly (ushort Bit, ushort Key)[] Buttons =
    [
        (Wire.PadA, BtnA),
        (Wire.PadB, BtnB),
        (Wire.PadX, BtnX),
        (Wire.PadY, BtnY),
        (Wire.PadLeftShoulder, BtnTl),
        (Wire.PadRightShoulder, BtnTr),
        (Wire.PadBack, BtnSelect),
        (Wire.PadStart, BtnStart),
        (Wire.PadGuide, BtnMode),
        (Wire.PadLeftThumb, BtnThumbL),
        (Wire.PadRightThumb, BtnThumbR),
    ];

    private readonly object _gate = new();
    private int _fd;
    private PadState _state;

    private UInputPad(int fd)
    {
        _fd = fd;
    }

    /// <summary>Creates the device. Null on failure, with the reason.</summary>
    public static UInputPad? Open(out string why)
    {
        int fd = UInputDevice.Open();
        if (fd < 0)
        {
            why = $"cannot open /dev/uinput: {LastError()}";
            return null;
        }

        bool made = SetBit(fd, UiSetEvBit, EvKey) && SetBit(fd, UiSetEvBit, EvAbs);
        foreach (var (_, key) in Buttons)
            made = made && SetBit(fd, UiSetKeyBit, key);

        // Sticks, triggers and the d-pad as the kernel's Xbox driver reports them: the d-pad is a
        // pair of axes there, not four buttons.
        made = made &&
               SetAxis(fd, AbsX, -32768, 32767, 128) && SetAxis(fd, AbsY, -32768, 32767, 128) &&
               SetAxis(fd, AbsRx, -32768, 32767, 128) && SetAxis(fd, AbsRy, -32768, 32767, 128) &&
               SetAxis(fd, AbsZ, 0, 255, 0) && SetAxis(fd, AbsRz, 0, 255, 0) &&
               SetAxis(fd, AbsHat0X, -1, 1, 0) && SetAxis(fd, AbsHat0Y, -1, 1, 0);

        // The ids of a wired Xbox 360 controller. They, not the name, are what Steam and SDL look a
        // controller up by, so the name is free to say what this really is.
        made = made && Create(fd, "Rayxel gamepad", 0x045e, 0x028e, 0x0110);

        if (!made)
        {
            why = $"the virtual controller could not be created: {LastError()}";
            CloseHandle(fd);
            return null;
        }
        why = "";
        return new UInputPad(fd);
    }

    /// <summary>Makes the device match <paramref name="next"/>, reporting only what differs from last time.</summary>
    public void Apply(in PadState next)
    {
        lock (_gate)
        {
            if (_fd < 0) return;
            PadState last = _state;
            bool changed = false;

            foreach ((ushort bit, ushort key) in Buttons)
            {
                if (((last.Buttons ^ next.Buttons) & bit) == 0) continue;
                Emit(_fd, EvKey, key, (next.Buttons & bit) != 0 ? 1 : 0);
                changed = true;
            }

            changed |= Axis(AbsHat0X, Hat(last.Buttons, Wire.PadLeft, Wire.PadRight), Hat(next.Buttons, Wire.PadLeft, Wire.PadRight));
            changed |= Axis(AbsHat0Y, Hat(last.Buttons, Wire.PadUp, Wire.PadDown), Hat(next.Buttons, Wire.PadUp, Wire.PadDown));
            changed |= Axis(AbsX, last.LeftX, next.LeftX);
            changed |= Axis(AbsY, Flip(last.LeftY), Flip(next.LeftY));
            changed |= Axis(AbsRx, last.RightX, next.RightX);
            changed |= Axis(AbsRy, Flip(last.RightY), Flip(next.RightY));
            changed |= Axis(AbsZ, last.LeftTrigger, next.LeftTrigger);
            changed |= Axis(AbsRz, last.RightTrigger, next.RightTrigger);

            // Everything above becomes one report: a game sees the state change at once.
            if (changed) Emit(_fd, EvSyn, SynReport, 0);
            _state = next;
        }
    }

    /// <summary>True while anything is held or pushed away from rest.</summary>
    public bool Active
    {
        get
        {
            lock (_gate) return _state != default;
        }
    }

    /// <summary>Lets go of everything: what has to happen when the viewer stops being heard from.</summary>
    public void Release() => Apply(default);

    /// <summary>Removes the device; Steam sees a controller being unplugged.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_fd < 0) return;
            Release();
            Destroy(_fd);
            _fd = -1;
        }
    }

    private bool Axis(ushort code, int before, int after)
    {
        if (before == after) return false;
        Emit(_fd, EvAbs, code, after);
        return true;
    }

    /// <summary>The wire has up as positive, like the controller's own API; Linux has it negative.</summary>
    private static int Flip(short value) => value == short.MinValue ? short.MaxValue : -value;

    private static int Hat(ushort state, ushort negative, ushort positive) =>
        ((state & positive) != 0 ? 1 : 0) - ((state & negative) != 0 ? 1 : 0);
}
