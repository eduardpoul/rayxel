using System.Runtime.InteropServices;
using Rayxel.Diagnostics;

namespace Rayxel.Native;

/// <summary>
/// The product mark as Win32 icon handles, for the window, the taskbar button and Alt-Tab.
/// <para>
/// The <c>.ico</c> is an embedded assembly resource rather than a file beside the
/// executable, for the same reason the NIS sources are: a single-file build has nowhere to
/// put loose files, and an executable that loses its icon when moved on its own is not one
/// file. <c>&lt;ApplicationIcon&gt;</c> in the project file covers what Explorer shows for
/// the executable; it does not reach a window the app creates itself.
/// </para>
/// <para>
/// Every image inside <c>rayxel.ico</c> is PNG-compressed, which rules out picking one apart
/// by hand — but <c>CreateIconFromResourceEx</c> takes PNG and DIB alike, so the work here is
/// only choosing which of the eight sizes to hand it.
/// </para>
/// </summary>
public static class AppIcon
{
    private static IntPtr _large, _small;
    private static bool _loaded;

    /// <summary>Title bar at large sizes, Alt-Tab, the taskbar button. Zero if unavailable.</summary>
    public static IntPtr Large { get { Load(); return _large; } }

    /// <summary>Title bar and the window's system menu. Zero if unavailable.</summary>
    public static IntPtr Small { get { Load(); return _small; } }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        var assembly = typeof(AppIcon).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("rayxel.ico", StringComparison.OrdinalIgnoreCase));
        if (name is null) { Log.Warn("rayxel.ico is not embedded; the window keeps the default icon"); return; }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var ico = buffer.ToArray();

        // The shell's own idea of the two sizes, which is DPI-aware — so a 200% display
        // gets the 32px and 64px images rather than one 16px bitmap stretched to fit. Both
        // metrics answer in virtualised units until the process has declared itself aware,
        // hence the call: this runs before the window exists, which is where that normally
        // happens.
        Win32.EnsureDpiAwareness();
        _large = Create(ico, Win32.GetSystemMetrics(Win32.SM_CXICON), Win32.GetSystemMetrics(Win32.SM_CYICON));
        _small = Create(ico, Win32.GetSystemMetrics(Win32.SM_CXSMICON), Win32.GetSystemMetrics(Win32.SM_CYSMICON));

        if (_large == IntPtr.Zero && _small == IntPtr.Zero)
            Log.Warn($"could not build a window icon from rayxel.ico ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>Releases the two handles. Called at shutdown; the OS would do it anyway.</summary>
    public static void Release()
    {
        if (_large != IntPtr.Zero) { Win32.DestroyIcon(_large); _large = IntPtr.Zero; }
        if (_small != IntPtr.Zero) { Win32.DestroyIcon(_small); _small = IntPtr.Zero; }
    }

    /// <summary>
    /// One image out of the file, at the size asked for. The .ico layout is a six-byte
    /// header followed by one sixteen-byte directory entry per image, then the images
    /// themselves at the offsets those entries carry.
    /// </summary>
    private static IntPtr Create(byte[] ico, int width, int height)
    {
        if (width <= 0 || height <= 0) return IntPtr.Zero;
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1) return IntPtr.Zero;

        int count = BitConverter.ToUInt16(ico, 4);
        if (count == 0 || ico.Length < 6 + count * 16) return IntPtr.Zero;

        int best = -1, bestWidth = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            // A zero in the width byte means 256: the field is one byte and 256 does not fit.
            int w = ico[entry] == 0 ? 256 : ico[entry];

            // Exactly right beats too big beats too small, in that order. Anything else
            // means scaling, and scaling down a larger image beats blowing a smaller one up.
            bool better = best < 0
                || (w >= width && (bestWidth < width || w < bestWidth))
                || (w < width && bestWidth < width && w > bestWidth);
            if (!better) continue;

            best = entry;
            bestWidth = w;
        }
        if (best < 0) return IntPtr.Zero;

        int length = BitConverter.ToInt32(ico, best + 8);
        int offset = BitConverter.ToInt32(ico, best + 12);
        if (length <= 0 || offset < 0 || (long)offset + length > ico.Length) return IntPtr.Zero;

        var pinned = GCHandle.Alloc(ico, GCHandleType.Pinned);
        try
        {
            // 0x00030000 is the icon format version every Windows since 3.0 writes; there
            // is no other value. LR_DEFAULTCOLOR (0) takes the display's depth.
            return Win32.CreateIconFromResourceEx(pinned.AddrOfPinnedObject() + offset, (uint)length,
                                                  true, 0x00030000, width, height, 0);
        }
        finally { pinned.Free(); }
    }
}
