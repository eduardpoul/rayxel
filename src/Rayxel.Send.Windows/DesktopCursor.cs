using Rayxel.Link;
using Rayxel.Link.Media;
using Vortice.DXGI;

namespace Rayxel.Send.Windows;

/// <summary>
/// The Windows mouse pointer, for putting into the picture. Desktop duplication never draws
/// it into the frames it hands over - the pointer is a hardware overlay that the display
/// lays over the desktop on its way out - and reports it beside each frame instead: where it
/// is, whether it shows, and what it looks like whenever that changes. The capture passes
/// that on through a <see cref="PointerFeed"/>, and this makes of it an overlay in the
/// frame's own colour coding and at the frame's scale.
/// <para>
/// Windows has three kinds of pointer. A colour one is a picture with coverage, which an
/// overlay is too. The other two can also invert what is under them - the text cursor
/// does, so that it shows on any background - which an overlay of fixed colours cannot do
/// by itself. So those pixels are made afresh for every frame from the frame's own pixels
/// beneath them, which the capture lends for the purpose (<see cref="PointerFeed.ShowFrame"/>).
/// </para>
/// </summary>
public sealed unsafe class DesktopCursor : ICursorSource
{
    /// <summary>No pointer is drawn larger than this, whatever the system says.</summary>
    private const int MaxSide = 256;

    private readonly PointerFeed _feed;
    private readonly CursorOverlay _overlay = new();

    // What the overlay was last made from, and for.
    private PointerShape? _madeFrom;
    private int _madeWidth, _madeHeight;
    private bool _madeBt601, _madeFullRange;
    private bool _empty;

    // For each of the overlay's pixels: the colour that covers it, how much it covers, and
    // how much of it inverts what is under it instead.
    private byte[] _luma = [], _cb = [], _cr = [], _alpha = [], _invert = [];
    private bool _inverts;

    private bool _saidDrawn, _saidBlind;

    internal DesktopCursor(PointerFeed feed) => _feed = feed;

    public CursorOverlay? Current(int frameWidth, int frameHeight, bool bt601, bool fullRange)
    {
        var state = _feed.Read();
        if (!state.Visible || state.Shape is not { } shape || state.SourceWidth <= 0 || state.SourceHeight <= 0 ||
            frameWidth <= 0 || frameHeight <= 0)
            return null;

        // A frame that is a smaller copy of the desktop gets a pointer smaller by as much,
        // so that it covers what it covers on the screen. Never larger: a pointer grown past
        // its own pixels only blurs.
        double sx = (double)frameWidth / state.SourceWidth, sy = (double)frameHeight / state.SourceHeight;
        double scale = Math.Min(1.0, Math.Min(sx, sy));
        int width = Math.Clamp((int)Math.Round(shape.Width * scale), 1, MaxSide);
        int height = Math.Clamp((int)Math.Round(shape.Height * scale), 1, MaxSide);

        if (!ReferenceEquals(shape, _madeFrom) || width != _madeWidth || height != _madeHeight ||
            bt601 != _madeBt601 || fullRange != _madeFullRange)
            Make(shape, width, height, bt601, fullRange);
        if (_empty) return null;

        // Duplication reports the shape's top left corner, not the hot spot: the hot spot is
        // already accounted for in where the system says to draw it.
        _overlay.X = (int)Math.Floor(state.X * sx);
        _overlay.Y = (int)Math.Floor(state.Y * sy);
        if (_inverts) Invert(frameWidth, frameHeight, fullRange);

        if (!_saidDrawn)
        {
            _saidDrawn = true;
            LinkLog.Info($"pointer: the first one is in the picture, {width}x{height}, {Kind(shape.Type)}");
        }
        return _overlay;
    }

    /// <summary>Nothing to read: the pointer arrives with the frames, through the capture.</summary>
    public void Poll()
    {
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// Makes the overlay for a shape at a size, in a colour coding. Each of its pixels is
    /// what it covers of the original, taken at 4x4 points across it - at full size that is
    /// the original pixel sixteen times over - as src/rayxel-send/src/cursor.c does it.
    /// </summary>
    private void Make(PointerShape shape, int width, int height, bool bt601, bool fullRange)
    {
        _madeFrom = shape;
        _madeWidth = width;
        _madeHeight = height;
        _madeBt601 = bt601;
        _madeFullRange = fullRange;

        int pixels = width * height;
        if (_luma.Length != pixels)
        {
            _luma = new byte[pixels];
            _cb = new byte[pixels];
            _cr = new byte[pixels];
            _alpha = new byte[pixels];
            _invert = new byte[pixels];
        }

        double kr = bt601 ? 0.299 : 0.2126, kb = bt601 ? 0.114 : 0.0722, kg = 1.0 - kr - kb;
        bool any = false, inverts = false;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint a = 0, r = 0, g = 0, b = 0, flipped = 0;
                for (int j = 0; j < 4; j++)
                {
                    int py = Math.Min((int)((y + (j + 0.5) / 4.0) * shape.Height / height), shape.Height - 1);
                    for (int i = 0; i < 4; i++)
                    {
                        int px = Math.Min((int)((x + (i + 0.5) / 4.0) * shape.Width / width), shape.Width - 1);
                        Sample(shape, px, py, out uint sr, out uint sg, out uint sb, out uint sa, out bool flip);
                        if (flip)
                        {
                            flipped++;
                            continue;
                        }
                        a += sa;
                        r += sr * sa;
                        g += sg * sa;
                        b += sb * sa;
                    }
                }

                int at = y * width + x;
                _alpha[at] = (byte)((a + 8) / 16);
                _invert[at] = (byte)((flipped * 255 + 8) / 16);
                any |= _alpha[at] != 0 || _invert[at] != 0;
                inverts |= _invert[at] != 0;

                if (a == 0)
                {
                    _luma[at] = (byte)(fullRange ? 0 : 16);
                    _cb[at] = _cr[at] = 128;
                    continue;
                }

                // The samples were summed weighed by their coverage; dividing by the
                // coverage gives back the colour itself, which is what an overlay holds.
                double red = Math.Min(1.0, r / (a * 255.0));
                double green = Math.Min(1.0, g / (a * 255.0));
                double blue = Math.Min(1.0, b / (a * 255.0));
                double grey = kr * red + kg * green + kb * blue;
                double blueness = (blue - grey) / (2.0 * (1.0 - kb)), redness = (red - grey) / (2.0 * (1.0 - kr));

                _luma[at] = ToByte(fullRange ? grey * 255.0 : 16.0 + grey * 219.0);
                _cb[at] = ToByte(128.0 + blueness * (fullRange ? 255.0 : 224.0));
                _cr[at] = ToByte(128.0 + redness * (fullRange ? 255.0 : 224.0));
            }
        }

        _empty = !any;
        _inverts = inverts;
        _overlay.Width = width;
        _overlay.Height = height;

        // Without inverting pixels the overlay is the same for every frame and is these very
        // planes; with them it is worked out anew each time, into planes of its own.
        if (inverts)
        {
            if (_overlay.Luma.Length != pixels || ReferenceEquals(_overlay.Luma, _luma))
            {
                _overlay.Luma = new byte[pixels];
                _overlay.Cb = new byte[pixels];
                _overlay.Cr = new byte[pixels];
                _overlay.Alpha = new byte[pixels];
            }
        }
        else
        {
            _overlay.Luma = _luma;
            _overlay.Cb = _cb;
            _overlay.Cr = _cr;
            _overlay.Alpha = _alpha;
        }
    }

    /// <summary>
    /// One pixel of the shape as a straight colour and its coverage, or as "invert what is
    /// under me". The three kinds, as IDXGIOutputDuplication::GetFramePointerShape gives them:
    /// <list type="bullet">
    /// <item>colour: 32-bit BGRA with coverage in the fourth byte;</item>
    /// <item>masked colour: 32-bit BGRA where the fourth byte is a mask - 0 puts the colour
    /// there, 0xFF puts the screen there XORed with the colour;</item>
    /// <item>monochrome: two 1-bit masks one above the other - the screen ANDed with the
    /// first and XORed with the second, which gives black, white, see-through or inverted.</item>
    /// </list>
    /// An XOR with any colour but none or white is rare and has no fixed result; it is taken
    /// to be an inversion, which is what it is nearly always meant as.
    /// </summary>
    private static void Sample(PointerShape shape, int x, int y, out uint r, out uint g, out uint b, out uint a, out bool invert)
    {
        r = g = b = a = 0;
        invert = false;
        byte[] pixels = shape.Pixels;

        if (shape.Type == PointerShapeType.Monochrome)
        {
            int and = y * shape.Pitch + x / 8, xor = (y + shape.Height) * shape.Pitch + x / 8;
            if (xor >= pixels.Length) return;
            int bit = 0x80 >> (x & 7);
            bool keep = (pixels[and] & bit) != 0, flip = (pixels[xor] & bit) != 0;
            if (keep)
            {
                invert = flip;
                return;
            }
            r = g = b = flip ? 255u : 0u;
            a = 255;
            return;
        }

        int at = y * shape.Pitch + x * 4;
        if (at + 3 >= pixels.Length) return;
        b = pixels[at];
        g = pixels[at + 1];
        r = pixels[at + 2];

        if (shape.Type == PointerShapeType.MaskedColor)
        {
            if (pixels[at + 3] == 0)
            {
                a = 255;
                return;
            }
            invert = (r | g | b) != 0;
            r = g = b = 0;
            return;
        }

        a = pixels[at + 3];
    }

    /// <summary>
    /// The overlay's planes for this frame: its fixed colours where it has them, and where it
    /// inverts, the inverse of the frame's own pixel there. Inverting RGB inverts luma about
    /// the middle of its range and turns chroma about 128, so this needs no trip through RGB.
    /// Only possible while the capture lends the frame - inside its callback; anywhere else
    /// the inverting part is drawn black, which shows on most of what it is drawn over.
    /// </summary>
    private void Invert(int frameWidth, int frameHeight, bool fullRange)
    {
        bool lent = _feed.TryBorrowFrame(frameWidth, frameHeight, out byte* frame, out int stride);
        if (!lent && !_saidBlind)
        {
            _saidBlind = true;
            LinkLog.Debug("pointer: an inverting pointer was asked for outside a capture's frame; it is drawn black there");
        }

        byte* chroma = frame + (long)stride * frameHeight;
        int lumaLow = fullRange ? 0 : 16, lumaHigh = fullRange ? 255 : 235;
        int chromaLow = fullRange ? 0 : 16, chromaHigh = fullRange ? 255 : 240;
        int width = _madeWidth, height = _madeHeight;
        byte[] luma = _overlay.Luma, cb = _overlay.Cb, cr = _overlay.Cr, alpha = _overlay.Alpha;

        for (int y = 0; y < height; y++)
        {
            int fy = _overlay.Y + y;
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                uint flip = _invert[at], cover = _alpha[at];
                if (flip == 0)
                {
                    luma[at] = _luma[at];
                    cb[at] = _cb[at];
                    cr[at] = _cr[at];
                    alpha[at] = (byte)cover;
                    continue;
                }

                int fx = _overlay.X + x;
                uint il = (uint)lumaLow, icb = 128, icr = 128;
                if (lent && fx >= 0 && fy >= 0 && fx < frameWidth && fy < frameHeight)
                {
                    il = (uint)Math.Clamp(lumaLow + lumaHigh - frame[(long)fy * stride + fx], lumaLow, lumaHigh);
                    byte* pair = chroma + (long)(fy / 2) * stride + (fx / 2) * 2;
                    icb = (uint)Math.Clamp(256 - pair[0], chromaLow, chromaHigh);
                    icr = (uint)Math.Clamp(256 - pair[1], chromaLow, chromaHigh);
                }

                // Part covered, part inverted, the rest left as it is: the overlay's colour
                // is the mixture of the first two and its coverage their sum.
                uint total = cover + flip;
                luma[at] = (byte)((_luma[at] * cover + il * flip + total / 2) / total);
                cb[at] = (byte)((_cb[at] * cover + icb * flip + total / 2) / total);
                cr[at] = (byte)((_cr[at] * cover + icr * flip + total / 2) / total);
                alpha[at] = (byte)Math.Min(255u, total);
            }
        }
    }

    private static byte ToByte(double value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)Math.Round(value);

    private static string Kind(PointerShapeType type) => type switch
    {
        PointerShapeType.Monochrome => "monochrome",
        PointerShapeType.MaskedColor => "masked colour",
        _ => "colour",
    };
}
