namespace Rayxel.Link.Media;

/// <summary>
/// A small picture to lay over a frame - the mouse pointer - already in the frame's own
/// colour coding: one luma, two chroma and one coverage sample for each of its pixels,
/// <see cref="Width"/> times <see cref="Height"/> of each. The colours are not multiplied by
/// the coverage. <see cref="X"/> and <see cref="Y"/> place its top left corner in the frame,
/// and it may hang over any edge.
/// </summary>
public sealed class CursorOverlay
{
    public int X, Y;
    public int Width, Height;
    public byte[] Luma = [], Cb = [], Cr = [], Alpha = [];

    /// <summary>
    /// Lays the overlay over an NV12 picture that has just been copied from
    /// <paramref name="source"/> into <paramref name="luma"/> and <paramref name="chroma"/>.
    /// What is under it is read from <paramref name="source"/> and only the result is written
    /// to the copy: the copy is usually an encoder's surface, memory that is slow to read back.
    /// </summary>
    public unsafe void BlendNv12(byte* source, int sourceStride, byte* luma, int lumaPitch, byte* chroma, int chromaPitch,
                                 int width, int height)
    {
        int x0 = Math.Max(X, 0), y0 = Math.Max(Y, 0);
        int x1 = Math.Min(X + Width, width), y1 = Math.Min(Y + Height, height);
        if (x0 >= x1 || y0 >= y1) return;

        fixed (byte* ol = Luma, ocb = Cb, ocr = Cr, oa = Alpha)
        {
            for (int y = y0; y < y1; y++)
            {
                int row = (y - Y) * Width;
                byte* under = source + (long)y * sourceStride;
                byte* output = luma + (long)y * lumaPitch;
                for (int x = x0; x < x1; x++)
                {
                    int at = row + (x - X);
                    uint a = oa[at];
                    if (a != 0) output[x] = (byte)((ol[at] * a + under[x] * (255 - a) + 127) / 255);
                }
            }

            // One chroma sample stands for a 2x2 block of pixels, and the overlay need not
            // start on an even one. So each sample is the frame's own, covered by as much of
            // the overlay as lies over its block: up to four of its pixels, weighed by coverage.
            byte* sourceChroma = source + (long)sourceStride * height;
            int cx1 = Math.Min((x1 + 1) / 2, width / 2), cy1 = Math.Min((y1 + 1) / 2, height / 2);

            for (int cy = y0 / 2; cy < cy1; cy++)
            {
                byte* under = sourceChroma + (long)cy * sourceStride;
                byte* output = chroma + (long)cy * chromaPitch;
                for (int cx = x0 / 2; cx < cx1; cx++)
                {
                    uint coverage = 0, cb = 0, cr = 0;
                    for (int j = 0; j < 2; j++)
                    {
                        int oy = cy * 2 + j - Y;
                        if (oy < 0 || oy >= Height) continue;
                        for (int i = 0; i < 2; i++)
                        {
                            int ox = cx * 2 + i - X;
                            if (ox < 0 || ox >= Width) continue;
                            int at = oy * Width + ox;
                            uint a = oa[at];
                            coverage += a;
                            cb += ocb[at] * a;
                            cr += ocr[at] * a;
                        }
                    }
                    if (coverage == 0) continue;

                    // Four pixels of full coverage make 1020.
                    output[cx * 2] = (byte)((cb + under[cx * 2] * (1020 - coverage) + 510) / 1020);
                    output[cx * 2 + 1] = (byte)((cr + under[cx * 2 + 1] * (1020 - coverage) + 510) / 1020);
                }
            }
        }
    }

    /// <summary>
    /// Copies a whole NV12 picture into <paramref name="luma"/> and <paramref name="chroma"/>
    /// with the overlay laid over it, when there is one. What encoders that upload from
    /// memory do with every frame.
    /// </summary>
    public static unsafe void CopyNv12(in CapturedFrame frame, byte* luma, int lumaPitch, byte* chroma, int chromaPitch,
                                       CursorOverlay? overlay)
    {
        int width = frame.Width, height = frame.Height, stride = frame.Stride;
        for (int y = 0; y < height; y++)
            Buffer.MemoryCopy(frame.Data + (long)y * stride, luma + (long)y * lumaPitch, width, width);
        byte* sourceChroma = frame.Data + (long)stride * height;
        for (int y = 0; y < height / 2; y++)
            Buffer.MemoryCopy(sourceChroma + (long)y * stride, chroma + (long)y * chromaPitch, width, width);
        overlay?.BlendNv12(frame.Data, stride, luma, lumaPitch, chroma, chromaPitch, width, height);
    }
}

/// <summary>
/// The sender's mouse pointer, for putting into the picture. gamescope leaves the pointer out
/// of the stream it gives a capture, so it is asked of its X servers and drawn in here.
/// </summary>
public interface ICursorSource : IDisposable
{
    /// <summary>
    /// The pointer as it should be laid over a frame of this size and colour coding, or null
    /// when none is showing. Valid until the next call. Called on the capture's thread.
    /// </summary>
    CursorOverlay? Current(int frameWidth, int frameHeight, bool bt601, bool fullRange);

    /// <summary>Reads what has come in since last time. Called now and then while no frames are being made.</summary>
    void Poll();
}
