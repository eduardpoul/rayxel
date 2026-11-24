using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.UInput;

/*
 * The kernel's uinput interface, as linux/uinput.h and linux/input.h declare it. Every value
 * and layout here was checked against those headers with static assertions.
 *
 * The ioctl numbers are _IO and _IOW of the uinput type 'U' (0x55), encoded as on x86 and ARM -
 * the generic encoding: the direction in the top two bits (write is 1), the argument's size in
 * the next fourteen, then the type, then the number. Other architectures (PowerPC, MIPS, SPARC)
 * encode them differently and are not served by these.
 */

/// <summary>struct uinput_setup: the device's ids and name. 92 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UInputSetup
{
    public const int NameSize = 80; // UINPUT_MAX_NAME_SIZE

    // struct input_id
    public ushort BusType, Vendor, Product, Version;
    public fixed byte Name[NameSize];
    public uint FfEffectsMax;
}

/// <summary>struct uinput_abs_setup: an axis and its range. 28 bytes; two bytes of padding follow the code.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UInputAbsSetup
{
    public ushort Code;

    // struct input_absinfo
    public int Value, Minimum, Maximum, Fuzz, Flat, Resolution;
}

/// <summary>
/// struct input_event on a 64-bit system: a struct timeval of two longs, then type, code and
/// value. 24 bytes. The time is left at zero; the kernel stamps every event it is written.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public nint Seconds, Microseconds;
    public ushort Type, Code;
    public int Value;
}

internal static unsafe partial class UInputDevice
{
    private const string Libc = "libc.so.6";

    public const int OWriteOnly = 1, OCloseOnExec = 0x80000; // O_WRONLY, O_CLOEXEC

    public const nuint UiDevCreate = 0x5501;    // _IO('U', 1)
    public const nuint UiDevDestroy = 0x5502;   // _IO('U', 2)
    public const nuint UiDevSetup = 0x405c5503; // _IOW('U', 3, struct uinput_setup), 92 bytes
    public const nuint UiAbsSetup = 0x401c5504; // _IOW('U', 4, struct uinput_abs_setup), 28 bytes
    public const nuint UiSetEvBit = 0x40045564; // _IOW('U', 100, int)
    public const nuint UiSetKeyBit = 0x40045565; // _IOW('U', 101, int)
    public const nuint UiSetRelBit = 0x40045566; // _IOW('U', 102, int)
    public const nuint UiSetAbsBit = 0x40045567; // _IOW('U', 103, int)

    public const ushort BusUsb = 0x03;
    public const ushort EvSyn = 0x00, EvKey = 0x01, EvRel = 0x02, EvAbs = 0x03;
    public const ushort SynReport = 0;

    public const ushort BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112, BtnSide = 0x113, BtnExtra = 0x114;
    public const ushort BtnA = 0x130, BtnB = 0x131, BtnX = 0x133, BtnY = 0x134, BtnTl = 0x136, BtnTr = 0x137;
    public const ushort BtnSelect = 0x13a, BtnStart = 0x13b, BtnMode = 0x13c, BtnThumbL = 0x13d, BtnThumbR = 0x13e;

    public const ushort RelX = 0x00, RelY = 0x01, RelHWheel = 0x06, RelWheel = 0x08, RelWheelHiRes = 0x0b,
                        RelHWheelHiRes = 0x0c;

    public const ushort AbsX = 0x00, AbsY = 0x01, AbsZ = 0x02, AbsRx = 0x03, AbsRy = 0x04, AbsRz = 0x05,
                        AbsHat0X = 0x10, AbsHat0Y = 0x11;

    // open(2) and ioctl(2) are variadic in C. Called with their fixed arguments only, as here,
    // the calling convention on x86-64 and AArch64 Linux is that of an ordinary function.
    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, nint argument);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, void* argument);

    [LibraryImport(Libc, EntryPoint = "write", SetLastError = true)]
    private static partial nint Write(int fd, void* buffer, nuint count);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int Close(int fd);

    /// <summary>The error of the last call that failed, in strerror's words.</summary>
    public static string LastError() => Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());

    /// <summary>A new handle on /dev/uinput, which is one device in the making; -1 on failure.</summary>
    public static int Open() => OpenFile("/dev/uinput", OWriteOnly | OCloseOnExec);

    public static bool SetBit(int fd, nuint request, int code) => Ioctl(fd, request, code) >= 0;

    public static bool SetAxis(int fd, ushort code, int minimum, int maximum, int flat)
    {
        var setup = new UInputAbsSetup { Code = code, Minimum = minimum, Maximum = maximum, Flat = flat };
        return SetBit(fd, UiSetAbsBit, code) && Ioctl(fd, UiAbsSetup, &setup) >= 0;
    }

    /// <summary>Names the device and makes it exist: from here the rest of the system sees it.</summary>
    public static bool Create(int fd, string name, ushort vendor, ushort product, ushort version)
    {
        var setup = new UInputSetup { BusType = BusUsb, Vendor = vendor, Product = product, Version = version };

        // As snprintf would: cut short to fit, always ended by a zero. The names are this
        // program's own, and plain ASCII.
        int length = Math.Min(name.Length, UInputSetup.NameSize - 1);
        for (int i = 0; i < length; i++) setup.Name[i] = (byte)name[i];

        return Ioctl(fd, UiDevSetup, &setup) >= 0 && Ioctl(fd, UiDevCreate, 0) >= 0;
    }

    public static void Destroy(int fd)
    {
        Ioctl(fd, UiDevDestroy, 0);
        Close(fd);
    }

    public static void CloseHandle(int fd) => Close(fd);

    /// <summary>
    /// One event. A write that fails leaves the device one event behind until the next state,
    /// which arrives within milliseconds and carries everything again.
    /// </summary>
    public static void Emit(int fd, ushort type, ushort code, int value)
    {
        var ev = new InputEvent { Type = type, Code = code, Value = value };
        Write(fd, &ev, (nuint)sizeof(InputEvent));
    }
}
