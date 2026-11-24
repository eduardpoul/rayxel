using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rayxel.Link;
using Rayxel.Link.Media;
using Rayxel.Send.Linux.X11;

namespace Rayxel.Send.Linux;

/// <summary>
/// The Deck's mouse pointer, for putting into the picture.
/// <para>
/// gamescope draws the pointer on the Deck's own screen and leaves it out of the stream it gives
/// to whoever captures: that stream is the windows and nothing else. So the picture a viewer gets
/// has no pointer in it unless it is put there. Nothing here is made up, though: whether there is
/// a pointer, where it is and what it looks like are all asked of gamescope's X servers, which is
/// where gamescope itself gets them.
/// </para>
/// <para>
/// Xlib is not safe to call from two threads at once, and <see cref="Current"/> comes on the
/// capture's thread while <see cref="Poll"/> and <see cref="Dispose"/> may come on another, so
/// every call into it here is made under one lock.
/// </para>
/// </summary>
public sealed unsafe class X11Cursor : ICursorSource
{
    // gamescope runs one X server for Steam and one for each game it keeps apart; the Deck has two.
    private const int MaxScreens = 4;

    // The first of them is gamescope's own: it is where gamescope says which of them the mouse is on.
    private const string FirstScreen = ":0";

    // The longest name a screen is known by, as gamescope can write one.
    private const int NameCapacity = 16;

    // No pointer is drawn larger than this, whatever an X server says.
    private const int MaxSide = 256;

    // The size of the window under the pointer is asked for again after this many frames even
    // though nothing said it had changed.
    private const int MeasureEvery = 30;

    private sealed class Screen
    {
        public string Name = "";
        public nint Display;
        public nuint Root;
        public int FixesEvent = -1; // the first of XFixes' event numbers, or -1 without XFixes
        public nuint VisibleAtom;

        // Set by Xlib's exit handler when the connection breaks; never touched again after.
        public byte* DeadFlag;
        public bool Dead => *DeadFlag != 0;

        // What gamescope last said, and whether it has since said something new.
        public bool Visible, VisibleStale, ImageStale;
    }

    private readonly object _gate = new();
    private readonly List<Screen> _screens = new(MaxScreens);
    private uint _screenHeight; // of the first screen: the Deck's picture at its full size

    private nuint _mouseDisplayAtom, _focusDisplayAtom, _focusedWindowAtom;
    private bool _focusStale;
    private Screen? _mouse;    // the screen the mouse is on
    private nuint _window;     // the window the picture is of, or that screen's root
    private uint _windowWidth, _windowHeight;
    private uint _sinceMeasured;

    // The pointer's picture as last made, and what it was made for.
    private Screen? _madeFor;
    private uint _madeHeight;
    private bool _madeBt601, _madeFullRange;
    private bool _empty;
    private int _hotX, _hotY;
    private readonly CursorOverlay _overlay = new();

    private bool _saidDrawn;
    private bool _disposed;

    private X11Cursor()
    {
    }

    /// <summary>
    /// Never fails for want of an X server: with none to ask there is simply never a pointer.
    /// Says which it is in one line. Null only when libX11 itself cannot be had, or is too old to
    /// be told not to end the program when a server goes away.
    /// </summary>
    public static X11Cursor? TryOpen()
    {
        if (!Xlib.Load(out string why))
        {
            LinkLog.Info($"pointer: {why}, so the picture will have no mouse pointer");
            return null;
        }

        var c = new X11Cursor();
        lock (c._gate)
        {
            // A write to an X server that has gone must fail, not end the process. The runtime
            // ignores SIGPIPE already; this says so again rather than depend on it.
            Xlib.Signal(Xlib.SigPipe, Xlib.SigIgnore);
            Xlib.SetErrorHandler(&OnError);
            Xlib.SetIOErrorHandler(&OnIOError);

            Screen? first = c.OpenScreen(FirstScreen);
            if (first == null)
            {
                LinkLog.Info(
                    $"pointer: gamescope's X server {FirstScreen} cannot be reached, so the picture will have no mouse pointer");
                return c;
            }

            c._mouseDisplayAtom = Atom(first.Display, "GAMESCOPE_MOUSE_FOCUS_DISPLAY\0"u8);
            c._focusDisplayAtom = Atom(first.Display, "GAMESCOPE_FOCUS_DISPLAY\0"u8);
            c._focusedWindowAtom = Atom(first.Display, "GAMESCOPE_FOCUSED_WINDOW\0"u8);
            c._screenHeight = (uint)Xlib.DisplayHeight(first.Display, Xlib.DefaultScreen(first.Display));
            c._focusStale = true;

            LinkLog.Info("pointer: gamescope leaves the mouse pointer out of the stream; it is put into the picture here, " +
                         "where and when gamescope shows it" +
                         (first.FixesEvent < 0 ? " (but this X server cannot say what it looks like)" : ""));
            return c;
        }
    }

    public CursorOverlay? Current(int frameWidth, int frameHeight, bool bt601, bool fullRange)
    {
        lock (_gate)
        {
            if (_disposed || _screens.Count == 0 || _screens[0].Dead) return null;

            Drain();
            if (_screens[0].Dead) return null;
            if (_focusStale || _mouse == null || _mouse.Dead) FindFocus();

            Screen s = _mouse!;
            if (s.Dead) return null;

            if (s.VisibleStale)
            {
                uint value = 0;
                s.VisibleStale = false;
                s.Visible = ReadCardinals(s, s.VisibleAtom, &value, 1) == 1 && value != 0;
            }
            if (!s.Visible) return null;

            uint width = (uint)Math.Max(frameWidth, 0), height = (uint)Math.Max(frameHeight, 0);
            if (s.ImageStale || _madeFor != s || _madeHeight != height || _madeBt601 != bt601 || _madeFullRange != fullRange)
                MakePicture(s, height, bt601, fullRange);
            if (_empty) return null;

            if (++_sinceMeasured >= MeasureEvery)
            {
                _sinceMeasured = 0;
                if (!Measure()) return null;
            }

            nuint root, child;
            int rootX, rootY, x, y;
            uint buttons;
            if (Xlib.QueryPointer(s.Display, _window, &root, &child, &rootX, &rootY, &x, &y, &buttons) == 0)
            {
                // The window has gone; by the next frame gamescope will have named another.
                _focusStale = true;
                return null;
            }

            // gamescope fits the window into the picture, as large as it goes with its shape kept,
            // and centres it. The pointer's place in the window follows.
            double fit = Math.Min((double)width / _windowWidth, (double)height / _windowHeight);
            double left = (width - _windowWidth * fit) / 2.0, top = (height - _windowHeight * fit) / 2.0;
            _overlay.X = (int)Math.Floor(x * fit + left) - _hotX;
            _overlay.Y = (int)Math.Floor(y * fit + top) - _hotY;

            if (!_saidDrawn)
            {
                _saidDrawn = true;
                LinkLog.Info($"pointer: the first one is in the picture, {_overlay.Width}x{_overlay.Height}, on screen {s.Name}");
            }
            return _overlay;
        }
    }

    public void Poll()
    {
        lock (_gate)
        {
            if (!_disposed) Drain();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Screen s in _screens)
            {
                Xlib.CloseDisplay(s.Display);
                NativeMemory.Free(s.DeadFlag);
            }
            _screens.Clear();
            _mouse = _madeFor = null;
        }
    }

    // A window can go between being named and being asked about. That is an answer, not a
    // failure, and Xlib's own handler would end the process over it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, void* error) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnIOError(nint display) => 0;

    // Xlib's answer to a broken connection is to exit. A picture without a pointer is the better
    // answer here.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnIOExit(nint display, void* user) => *(byte*)user = 1;

    private static nuint Atom(nint display, ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return Xlib.InternAtom(display, p, Xlib.False);
    }

    private Screen? OpenScreen(string name)
    {
        foreach (Screen known in _screens)
            if (known.Name == name) return known;
        if (_screens.Count == MaxScreens || name.Length >= NameCapacity) return null;

        byte* text = stackalloc byte[NameCapacity];
        for (int i = 0; i < name.Length; i++) text[i] = (byte)name[i];
        text[name.Length] = 0;

        nint display = Xlib.OpenDisplay(text);
        if (display == 0) return null;

        var s = new Screen
        {
            Name = name,
            Display = display,
            Root = Xlib.DefaultRootWindow(display),
            DeadFlag = (byte*)NativeMemory.AllocZeroed(1),
        };
        _screens.Add(s);
        Xlib.SetIOErrorExitHandler(display, &OnIOExit, s.DeadFlag);

        s.VisibleAtom = Atom(display, "GAMESCOPE_CURSOR_VISIBLE_FEEDBACK\0"u8);
        Xlib.SelectInput(display, s.Root, Xlib.PropertyChangeMask);
        int fixesEvent, fixesError;
        if (Xlib.HaveFixes && Xlib.FixesQueryExtension(display, &fixesEvent, &fixesError) != 0)
        {
            s.FixesEvent = fixesEvent;
            Xlib.FixesSelectCursorInput(display, s.Root, Xlib.XFixesDisplayCursorNotifyMask);
        }

        s.VisibleStale = s.ImageStale = true;
        return s;
    }

    /// <summary>A CARDINAL property of a screen's root window: how many of its values were read.</summary>
    private static int ReadCardinals(Screen s, nuint atom, uint* output, int capacity)
    {
        nuint type, count, left;
        int format, n = 0;
        byte* data = null;

        if (Xlib.GetWindowProperty(s.Display, s.Root, atom, 0, capacity, Xlib.False, Xlib.XaCardinal, &type, &format, &count,
                &left, &data) != Xlib.Success)
            return 0;
        if (data == null) return 0;

        // Xlib hands 32-bit values over as longs.
        if (format == 32)
            for (; n < capacity && n < (int)count; n++) output[n] = (uint)((nuint*)data)[n];
        Xlib.Free(data);
        return n;
    }

    /// <summary>
    /// gamescope names a screen by writing the characters of its name, ":1", into a property that
    /// is declared to hold numbers. So the name is read back out of the numbers.
    /// </summary>
    private static string? ReadScreenName(Screen s, nuint atom)
    {
        uint* words = stackalloc uint[8];
        int n = ReadCardinals(s, atom, words, 8);
        Span<char> name = stackalloc char[NameCapacity];
        int length = 0;

        for (int i = 0; i < n; i++)
        {
            for (int b = 0; b < 4; b++)
            {
                byte ch = (byte)(words[i] >> (8 * b));
                if (ch == 0 || length + 1 == NameCapacity) goto done;
                name[length++] = (char)ch;
            }
        }
        done:
        return length > 0 ? new string(name[..length]) : null;
    }

    /// <summary>Reads what a screen has had to say, which is only ever "something changed".</summary>
    private void Drain()
    {
        XEvent ev;
        foreach (Screen s in _screens)
        {
            while (!s.Dead && Xlib.Pending(s.Display) > 0)
            {
                Xlib.NextEvent(s.Display, &ev);

                if (s.FixesEvent >= 0 && ev.Type == s.FixesEvent + Xlib.XFixesCursorNotify)
                {
                    s.ImageStale = true;
                }
                else if (ev.Type == Xlib.PropertyNotify)
                {
                    nuint atom = ev.PropertyAtom;
                    if (atom == s.VisibleAtom)
                        s.VisibleStale = true;
                    else if (s == _screens[0] &&
                             (atom == _mouseDisplayAtom || atom == _focusDisplayAtom || atom == _focusedWindowAtom))
                        _focusStale = true;
                }
            }
        }
    }

    /// <summary>Which screen the mouse is on, and which window there the picture is of.</summary>
    private void FindFocus()
    {
        Screen first = _screens[0];
        uint window = 0;

        _focusStale = false;
        _mouse = first;
        if (ReadScreenName(first, _mouseDisplayAtom) is { } name)
        {
            Screen? s = OpenScreen(name);
            if (s != null && !s.Dead) _mouse = s;
        }

        // The pointer's place is counted from the window being shown, when the mouse is on the
        // same screen as that window, and from the screen's corner otherwise.
        _window = _mouse.Root;
        if (ReadScreenName(first, _focusDisplayAtom) is { } shown && shown == _mouse.Name &&
            ReadCardinals(first, _focusedWindowAtom, &window, 1) == 1 && window != 0)
            _window = window;

        _sinceMeasured = MeasureEvery;
    }

    private bool Measure()
    {
        for (;;)
        {
            nuint root;
            int x, y;
            uint width = 0, height = 0, border, depth;

            if (Xlib.GetGeometry(_mouse!.Display, _window, &root, &x, &y, &width, &height, &border, &depth) != 0 &&
                width != 0 && height != 0)
            {
                _windowWidth = width;
                _windowHeight = height;
                return true;
            }
            if (_window == _mouse.Root) return false;
            _window = _mouse.Root; // the window has gone
        }
    }

    /// <summary>C's lround: halves go away from zero.</summary>
    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static byte ToByte(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)Round(value);

    /// <summary>
    /// Asks the screen what its pointer looks like and makes of it a picture to lay over a frame
    /// <paramref name="frameHeight"/> tall, in that frame's colour coding.
    /// </summary>
    private void MakePicture(Screen s, uint frameHeight, bool bt601, bool fullRange)
    {
        _madeFor = s;
        _madeHeight = frameHeight;
        _madeBt601 = bt601;
        _madeFullRange = fullRange;
        _empty = true;
        s.ImageStale = false;

        XFixesCursorImage* image = s.FixesEvent >= 0 ? Xlib.FixesGetCursorImage(s.Display) : null;
        if (image == null) return;
        try
        {
            if (image->Width == 0 || image->Height == 0) return;
            Convert(image, frameHeight, bt601, fullRange);
        }
        finally
        {
            Xlib.Free(image);
        }
    }

    private void Convert(XFixesCursorImage* image, uint frameHeight, bool bt601, bool fullRange)
    {
        uint imageWidth = image->Width, imageHeight = image->Height;

        // gamescope shows the pointer on the Deck's screen as large as its picture is. A frame
        // that is a smaller copy of that screen gets a pointer smaller by as much.
        double scale = _screenHeight != 0 ? (double)frameHeight / _screenHeight : 1.0;
        if (scale > 1.0) scale = 1.0;
        uint width = (uint)Round(imageWidth * scale), height = (uint)Round(imageHeight * scale);
        width = Math.Clamp(width, 1, MaxSide);
        height = Math.Clamp(height, 1, MaxSide);

        int pixels = (int)(width * height);
        CursorOverlay o = _overlay;
        if (o.Alpha.Length != pixels)
        {
            o.Luma = new byte[pixels];
            o.Cb = new byte[pixels];
            o.Cr = new byte[pixels];
            o.Alpha = new byte[pixels];
        }
        byte[] luma = o.Luma, cb = o.Cb, cr = o.Cr, alpha = o.Alpha;

        double kr = bt601 ? 0.299 : 0.2126, kb = bt601 ? 0.114 : 0.0722, kg = 1.0 - kr - kb;
        bool any = false;

        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                // What this pixel covers of the original, taken at 4x4 points across it. At full
                // size that is the original pixel sixteen times over.
                uint a = 0, r = 0, g = 0, b = 0;
                for (int j = 0; j < 4; j++)
                {
                    uint sy = (uint)((y + (j + 0.5) / 4.0) * imageHeight / height);
                    if (sy >= imageHeight) sy = imageHeight - 1;
                    for (int i = 0; i < 4; i++)
                    {
                        uint sx = (uint)((x + (i + 0.5) / 4.0) * imageWidth / width);
                        if (sx >= imageWidth) sx = imageWidth - 1;
                        uint p = (uint)image->Pixels[(nuint)sy * imageWidth + sx];
                        a += p >> 24;
                        r += (p >> 16) & 255;
                        g += (p >> 8) & 255;
                        b += p & 255;
                    }
                }

                int at = (int)(y * width + x);
                alpha[at] = (byte)((a + 8) / 16);
                if (alpha[at] == 0)
                {
                    luma[at] = 0;
                    cb[at] = cr[at] = 128;
                    continue;
                }
                any = true;

                // X gives the colours already multiplied by the coverage.
                double red = Math.Min(1.0, (double)r / a), green = Math.Min(1.0, (double)g / a),
                       blue = Math.Min(1.0, (double)b / a);
                double grey = kr * red + kg * green + kb * blue;
                double blueness = (blue - grey) / (2.0 * (1.0 - kb)), redness = (red - grey) / (2.0 * (1.0 - kr));

                luma[at] = ToByte(fullRange ? grey * 255.0 : 16.0 + grey * 219.0);
                cb[at] = ToByte(128.0 + blueness * (fullRange ? 255.0 : 224.0));
                cr[at] = ToByte(128.0 + redness * (fullRange ? 255.0 : 224.0));
            }
        }

        _hotX = (int)Round((double)image->XHot * width / imageWidth);
        _hotY = (int)Round((double)image->YHot * height / imageHeight);
        o.Width = (int)width;
        o.Height = (int)height;
        _empty = !any;
    }
}
