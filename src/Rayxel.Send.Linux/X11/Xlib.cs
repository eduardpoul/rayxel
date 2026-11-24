using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.X11;

/*
 * The parts of libX11 and libXfixes the pointer is asked through, with the layouts of
 * X11/Xlib.h and X11/extensions/Xfixes.h on a 64-bit system: an XID - a Window, an Atom - is
 * an unsigned long, a Bool an int. Both libraries are loaded when first asked for rather than
 * linked, so that a machine without them still sends a picture, only without the pointer.
 */

/// <summary>XFixesCursorImage at XFIXES_MAJOR 2 and later: 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XFixesCursorImage
{
    public short X, Y;
    public ushort Width, Height;
    public ushort XHot, YHot;
    public nuint CursorSerial;

    /// <summary>
    /// Width times height ARGB values, alpha in the top byte and the colours already multiplied
    /// by it - each in an unsigned long, so eight bytes apiece on a 64-bit system.
    /// </summary>
    public nuint* Pixels;

    public nuint Atom;
    public byte* Name;
}

/// <summary>XEvent: a union of every event, as large as 24 longs. Only the type is read from all of them.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XEvent
{
    public fixed long Pad[24];

    public int Type
    {
        get
        {
            fixed (long* p = Pad) return *(int*)p;
        }
    }

    /// <summary>XPropertyEvent.atom: after the type, serial, send_event, display and window.</summary>
    public nuint PropertyAtom
    {
        get
        {
            fixed (long* p = Pad) return *(nuint*)((byte*)p + 40);
        }
    }
}

internal static unsafe partial class Xlib
{
    public const int Success = 0, False = 0;
    public const int PropertyNotify = 28;
    public const nint PropertyChangeMask = 1 << 22;
    public const nuint XaCardinal = 6;

    // X11/extensions/xfixeswire.h: event numbers count from the extension's first one.
    public const int XFixesCursorNotify = 1;
    public const nuint XFixesDisplayCursorNotifyMask = 1;

    private static readonly object Gate = new();
    private static bool _tried, _loaded;
    private static string _problem = "";
    private static nint _x11, _xfixes;

    public static delegate* unmanaged[Cdecl]<byte*, nint> OpenDisplay;
    public static delegate* unmanaged[Cdecl]<nint, int> CloseDisplay;
    public static delegate* unmanaged[Cdecl]<nint, nuint> DefaultRootWindow;
    public static delegate* unmanaged[Cdecl]<nint, int> DefaultScreen;
    public static delegate* unmanaged[Cdecl]<nint, int, int> DisplayHeight;
    public static delegate* unmanaged[Cdecl]<nint, byte*, int, nuint> InternAtom;
    public static delegate* unmanaged[Cdecl]<nint, nuint, nint, int> SelectInput;
    public static delegate* unmanaged[Cdecl]<nint, int> Pending;
    public static delegate* unmanaged[Cdecl]<nint, XEvent*, int> NextEvent;
    public static delegate* unmanaged[Cdecl]<void*, int> Free;

    /// <summary>XGetWindowProperty(display, window, property, offset, length, delete, type, &amp;type, &amp;format, &amp;count, &amp;left, &amp;data).</summary>
    public static delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int>
        GetWindowProperty;

    /// <summary>XGetGeometry(display, drawable, &amp;root, &amp;x, &amp;y, &amp;width, &amp;height, &amp;border, &amp;depth).</summary>
    public static delegate* unmanaged[Cdecl]<nint, nuint, nuint*, int*, int*, uint*, uint*, uint*, uint*, int> GetGeometry;

    /// <summary>XQueryPointer(display, window, &amp;root, &amp;child, &amp;root_x, &amp;root_y, &amp;x, &amp;y, &amp;buttons).</summary>
    public static delegate* unmanaged[Cdecl]<nint, nuint, nuint*, nuint*, int*, int*, int*, int*, uint*, int> QueryPointer;

    public static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, void*, int>, nint> SetErrorHandler;
    public static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, int>, nint> SetIOErrorHandler;

    /// <summary>libX11 1.7 and later: what to do instead of exiting when a connection breaks.</summary>
    public static delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void*, void>, void*, void>
        SetIOErrorExitHandler;

    // Null when libXfixes is not there; every X server is then one that cannot say what its pointer looks like.
    public static delegate* unmanaged[Cdecl]<nint, int*, int*, int> FixesQueryExtension;
    public static delegate* unmanaged[Cdecl]<nint, nuint, nuint, void> FixesSelectCursorInput;
    public static delegate* unmanaged[Cdecl]<nint, XFixesCursorImage*> FixesGetCursorImage;

    public static bool HaveFixes => FixesGetCursorImage != null;

    /// <summary>Loads libX11 and, if it is there, libXfixes. False, with the reason, when the pointer cannot be had at all.</summary>
    public static bool Load(out string why)
    {
        lock (Gate)
        {
            if (!_tried)
            {
                _tried = true;
                _loaded = TryLoad();
            }
            why = _problem;
            return _loaded;
        }
    }

    private static bool TryLoad()
    {
        if (!NativeLibrary.TryLoad("libX11.so.6", out _x11))
        {
            _problem = "libX11.so.6 cannot be loaded";
            return false;
        }
        try
        {
            OpenDisplay = (delegate* unmanaged[Cdecl]<byte*, nint>)Export(_x11, "XOpenDisplay");
            CloseDisplay = (delegate* unmanaged[Cdecl]<nint, int>)Export(_x11, "XCloseDisplay");
            DefaultRootWindow = (delegate* unmanaged[Cdecl]<nint, nuint>)Export(_x11, "XDefaultRootWindow");
            DefaultScreen = (delegate* unmanaged[Cdecl]<nint, int>)Export(_x11, "XDefaultScreen");
            DisplayHeight = (delegate* unmanaged[Cdecl]<nint, int, int>)Export(_x11, "XDisplayHeight");
            InternAtom = (delegate* unmanaged[Cdecl]<nint, byte*, int, nuint>)Export(_x11, "XInternAtom");
            SelectInput = (delegate* unmanaged[Cdecl]<nint, nuint, nint, int>)Export(_x11, "XSelectInput");
            Pending = (delegate* unmanaged[Cdecl]<nint, int>)Export(_x11, "XPending");
            NextEvent = (delegate* unmanaged[Cdecl]<nint, XEvent*, int>)Export(_x11, "XNextEvent");
            Free = (delegate* unmanaged[Cdecl]<void*, int>)Export(_x11, "XFree");
            GetWindowProperty =
                (delegate* unmanaged[Cdecl]<nint, nuint, nuint, nint, nint, int, nuint, nuint*, int*, nuint*, nuint*, byte**, int>)
                Export(_x11, "XGetWindowProperty");
            GetGeometry = (delegate* unmanaged[Cdecl]<nint, nuint, nuint*, int*, int*, uint*, uint*, uint*, uint*, int>)
                Export(_x11, "XGetGeometry");
            QueryPointer = (delegate* unmanaged[Cdecl]<nint, nuint, nuint*, nuint*, int*, int*, int*, int*, uint*, int>)
                Export(_x11, "XQueryPointer");
            SetErrorHandler = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, void*, int>, nint>)
                Export(_x11, "XSetErrorHandler");
            SetIOErrorHandler = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<nint, int>, nint>)
                Export(_x11, "XSetIOErrorHandler");
        }
        catch (EntryPointNotFoundException ex)
        {
            _problem = ex.Message;
            return false;
        }

        // Without it a broken connection ends the process, which is worse than having no pointer.
        if (!NativeLibrary.TryGetExport(_x11, "XSetIOErrorExitHandler", out nint exit))
        {
            _problem = "libX11 is older than 1.7 and would end the program when an X server goes away";
            return false;
        }
        SetIOErrorExitHandler = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, void*, void>, void*, void>)exit;

        if (NativeLibrary.TryLoad("libXfixes.so.3", out _xfixes) &&
            NativeLibrary.TryGetExport(_xfixes, "XFixesQueryExtension", out nint query) &&
            NativeLibrary.TryGetExport(_xfixes, "XFixesSelectCursorInput", out nint select) &&
            NativeLibrary.TryGetExport(_xfixes, "XFixesGetCursorImage", out nint image))
        {
            FixesQueryExtension = (delegate* unmanaged[Cdecl]<nint, int*, int*, int>)query;
            FixesSelectCursorInput = (delegate* unmanaged[Cdecl]<nint, nuint, nuint, void>)select;
            FixesGetCursorImage = (delegate* unmanaged[Cdecl]<nint, XFixesCursorImage*>)image;
        }
        return true;
    }

    private static nint Export(nint library, string name) =>
        NativeLibrary.TryGetExport(library, name, out nint address)
            ? address
            : throw new EntryPointNotFoundException($"libX11 has no {name}");

    // SIGPIPE, and SIG_IGN as a handler: the values on every Linux this runs on.
    public const int SigPipe = 13;
    public const nint SigIgnore = 1;

    [LibraryImport("libc.so.6", EntryPoint = "signal")]
    public static partial nint Signal(int signal, nint handler);
}
