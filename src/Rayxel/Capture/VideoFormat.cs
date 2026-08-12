using System.Buffers.Binary;

namespace Rayxel.Capture;

/// <summary>How the pixels reach us — this is the distinction that costs input lag.</summary>
public enum Compression
{
    /// <summary>Raw pixels. Fixed frame size, no codec in the path.</summary>
    None,
    /// <summary>Encoded on the device, decoded on the host. Costs latency and quality.</summary>
    Lossy
}

/// <summary>
/// Metadata for one video subtype. Most MF video subtypes are
/// {FOURCC}-0000-0010-8000-00AA00389B71, so the FOURCC is decoded from the GUID
/// rather than matched against a table; the table only supplies the extra facts
/// (bit depth, compression, chroma) that the GUID cannot tell us.
/// </summary>
public sealed record VideoFormat(
    Guid Subtype,
    string Name,
    double BytesPerPixel,
    int BitsPerComponent,
    Compression Compression,
    string Chroma)
{
    /// <summary>True when the format can carry more than 8 bits per component.</summary>
    public bool IsHighBitDepth => BitsPerComponent > 8;

    /// <summary>Uncompressed frame size in bytes. Meaningless for lossy formats.</summary>
    public long BytesPerFrame(int width, int height) => (long)(width * (double)height * BytesPerPixel);

    public override string ToString() => Name;

    // The MF video subtype namespace: xxxxxxxx-0000-0010-8000-00AA00389B71
    private static readonly byte[] MfVideoTail = [0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    private sealed record Facts(string Name, double Bpp, int Bits, Compression Comp, string Chroma);

    // Keyed by FOURCC (or by D3DFORMAT ordinal for the RGB types, which MF encodes
    // as small integers in the same GUID namespace).
    private static readonly Dictionary<uint, Facts> Table = new()
    {
        [Fourcc("NV12")] = new("NV12", 1.5, 8, Compression.None, "4:2:0"),
        [Fourcc("YV12")] = new("YV12", 1.5, 8, Compression.None, "4:2:0"),
        [Fourcc("I420")] = new("I420", 1.5, 8, Compression.None, "4:2:0"),
        [Fourcc("IYUV")] = new("IYUV", 1.5, 8, Compression.None, "4:2:0"),
        [Fourcc("YUY2")] = new("YUY2", 2.0, 8, Compression.None, "4:2:2"),
        [Fourcc("UYVY")] = new("UYVY", 2.0, 8, Compression.None, "4:2:2"),
        [Fourcc("YVYU")] = new("YVYU", 2.0, 8, Compression.None, "4:2:2"),
        [Fourcc("NV11")] = new("NV11", 1.5, 8, Compression.None, "4:1:1"),
        [Fourcc("AYUV")] = new("AYUV", 4.0, 8, Compression.None, "4:4:4"),
        [Fourcc("P010")] = new("P010", 3.0, 10, Compression.None, "4:2:0"),
        [Fourcc("P016")] = new("P016", 3.0, 16, Compression.None, "4:2:0"),
        [Fourcc("P210")] = new("P210", 4.0, 10, Compression.None, "4:2:2"),
        [Fourcc("Y210")] = new("Y210", 4.0, 10, Compression.None, "4:2:2"),
        [Fourcc("Y410")] = new("Y410", 4.0, 10, Compression.None, "4:4:4"),
        [Fourcc("v210")] = new("v210", 2.67, 10, Compression.None, "4:2:2"),
        [Fourcc("MJPG")] = new("MJPEG", 0.0, 8, Compression.Lossy, "4:2:0"),
        [Fourcc("H264")] = new("H.264", 0.0, 8, Compression.Lossy, "4:2:0"),
        [Fourcc("HEVC")] = new("HEVC", 0.0, 8, Compression.Lossy, "4:2:0"),
        [Fourcc("hvc1")] = new("HEVC", 0.0, 8, Compression.Lossy, "4:2:0"),

        // MFVideoFormat_RGB* reuse D3DFORMAT ordinals in the same GUID namespace.
        [20] = new("RGB24", 3.0, 8, Compression.None, "RGB"),
        [21] = new("ARGB32", 4.0, 8, Compression.None, "RGB"),
        [22] = new("RGB32", 4.0, 8, Compression.None, "RGB"),
        [23] = new("RGB565", 2.0, 6, Compression.None, "RGB"),
        [24] = new("RGB555", 2.0, 5, Compression.None, "RGB"),
        [50] = new("RGB10A2", 4.0, 10, Compression.None, "RGB"),
    };

    public static VideoFormat FromSubtype(Guid subtype)
    {
        if (TryGetMfCode(subtype, out uint code) && Table.TryGetValue(code, out var f))
            return new VideoFormat(subtype, f.Name, f.Bpp, f.Bits, f.Comp, f.Chroma);

        // Unknown but still in the MF namespace: surface the FOURCC so it can be
        // identified later instead of being hidden behind "unknown".
        if (TryGetMfCode(subtype, out code))
        {
            var tag = DecodeFourcc(code);
            var name = tag is null ? $"D3DFMT_{code}" : tag;
            return new VideoFormat(subtype, name, 0.0, 0, Compression.None, "?");
        }

        return new VideoFormat(subtype, subtype.ToString(), 0.0, 0, Compression.None, "?");
    }

    /// <summary>True when the GUID lives in the MF video subtype namespace; yields its leading 32 bits.</summary>
    private static bool TryGetMfCode(Guid g, out uint code)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        code = BinaryPrimitives.ReadUInt32LittleEndian(b);
        // bytes 4..7 are Data2/Data3 (0x0000, 0x0010), bytes 8..15 are Data4.
        var ok = b[4] == 0x00 && b[5] == 0x00 && b[6] == 0x10 && b[7] == 0x00
                 && b[8..].SequenceEqual(MfVideoTail);
        if (!ok) code = 0;
        return ok;
    }

    private static uint Fourcc(string tag) =>
        (uint)(tag[0] | (tag[1] << 8) | (tag[2] << 16) | (tag[3] << 24));

    /// <summary>Renders a 32-bit code as a FOURCC when all four bytes are printable ASCII.</summary>
    private static string? DecodeFourcc(uint code)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            var ch = (char)((code >> (i * 8)) & 0xFF);
            if (ch is < ' ' or > '~') return null;
            c[i] = ch;
        }
        return new string(c);
    }
}
