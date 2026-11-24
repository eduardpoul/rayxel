using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.Va;

// The structs of va.h this program fills in or reads, as libva 1.17's headers lay them out on
// x86-64. Each has its C size, and each named field the offset a program compiled against
// those headers printed with offsetof; only the fields used here are named, and everything
// else stays zero, as the C code's designated initialisers leave it. libva keeps this layout
// from version to version - new fields come out of the va_reserved padding at each struct's
// end - so the newer libva of SteamOS reads them the same.
//
// The field names are libva's, so that each can be found in its header.

/// <summary>
/// Puts values into the bitfield unions libva's structs are full of. GCC on x86-64 packs a
/// struct of bitfields from the least significant bit up, in the order they are declared, so
/// a field's place is the sum of the widths declared before it. The places used here were
/// printed by the same probe as the offsets, by setting each field alone and reading the
/// union's <c>value</c>.
/// </summary>
internal static class VaBits
{
    /// <summary>The low <paramref name="width"/> bits of <paramref name="value"/>, placed at bit <paramref name="shift"/>.</summary>
    public static uint Field(uint value, int shift, int width) => (value & ((1u << width) - 1)) << shift;

    /// <summary>A one-bit field at bit <paramref name="shift"/>.</summary>
    public static uint Flag(bool set, int shift) => set ? 1u << shift : 0u;
}

/// <summary>VAConfigAttrib.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct VAConfigAttrib
{
    [FieldOffset(0)] public int type; // VAConfigAttribType
    [FieldOffset(4)] public uint value;
}

/// <summary>VASurfaceAttrib, with its VAGenericValue laid out inline (the union in it is eight bytes, at 16).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct VASurfaceAttrib
{
    [FieldOffset(0)] public int type;         // VASurfaceAttribType
    [FieldOffset(4)] public uint flags;
    [FieldOffset(8)] public int value_type;   // value.type, a VAGenericValueType
    [FieldOffset(16)] public int value_i;     // value.value.i
}

/// <summary>VAImageFormat.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct VAImageFormat
{
    [FieldOffset(0)] public uint fourcc;
    [FieldOffset(4)] public uint byte_order;
    [FieldOffset(8)] public uint bits_per_pixel;
    [FieldOffset(12)] public uint depth;
    [FieldOffset(16)] public uint red_mask;
    [FieldOffset(20)] public uint green_mask;
    [FieldOffset(24)] public uint blue_mask;
    [FieldOffset(28)] public uint alpha_mask;
}

/// <summary>VAImage.</summary>
[StructLayout(LayoutKind.Explicit, Size = 120)]
internal unsafe struct VAImage
{
    [FieldOffset(0)] public uint image_id;
    [FieldOffset(4)] public VAImageFormat format;
    [FieldOffset(52)] public uint buf;
    [FieldOffset(56)] public ushort width;
    [FieldOffset(58)] public ushort height;
    [FieldOffset(60)] public uint data_size;
    [FieldOffset(64)] public uint num_planes;
    [FieldOffset(68)] public fixed uint pitches[3];
    [FieldOffset(80)] public fixed uint offsets[3];
}

/// <summary>VACodedBufferSegment: one piece of the coded buffer, in a list the driver links.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct VACodedBufferSegment
{
    [FieldOffset(0)] public uint size;
    [FieldOffset(4)] public uint bit_offset;
    [FieldOffset(8)] public uint status;
    [FieldOffset(16)] public void* buf;
    [FieldOffset(24)] public VACodedBufferSegment* next; // void * in the header
}

/// <summary>VAEncPackedHeaderParameterBuffer.</summary>
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct VAEncPackedHeaderParameterBuffer
{
    [FieldOffset(0)] public uint type; // VAEncPackedHeaderType
    [FieldOffset(4)] public uint bit_length;
    [FieldOffset(8)] public byte has_emulation_bytes;
}

/// <summary>
/// VAEncMiscParameterBuffer: the type, and then the parameter itself, as the flexible
/// array <c>data</c> that begins right after it, at 4.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 4)]
internal struct VAEncMiscParameterBuffer
{
    [FieldOffset(0)] public uint type; // VAEncMiscParameterType
}

/// <summary>VAEncMiscParameterRateControl.</summary>
[StructLayout(LayoutKind.Explicit, Size = 60)]
internal struct VAEncMiscParameterRateControl
{
    [FieldOffset(0)] public uint bits_per_second;
    [FieldOffset(4)] public uint target_percentage;
    [FieldOffset(8)] public uint window_size;
    [FieldOffset(12)] public uint initial_qp;
    [FieldOffset(16)] public uint min_qp;
    [FieldOffset(20)] public uint basic_unit_size;
    [FieldOffset(24)] public uint rc_flags;
    [FieldOffset(28)] public uint ICQ_quality_factor;
    [FieldOffset(32)] public uint max_qp;
    [FieldOffset(36)] public uint quality_factor;
    [FieldOffset(40)] public uint target_frame_size;

    /// <summary>
    /// rc_flags.bits: reset is bit 0, disable_frame_skip bit 1, disable_bit_stuffing bit 2;
    /// mb_rate_control, temporal_id and the rest above them stay 0.
    /// </summary>
    public static uint RcFlags(bool disableFrameSkip, bool disableBitStuffing) =>
        VaBits.Flag(disableFrameSkip, 1) | VaBits.Flag(disableBitStuffing, 2);
}

/// <summary>VAEncMiscParameterHRD.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct VAEncMiscParameterHRD
{
    [FieldOffset(0)] public uint initial_buffer_fullness;
    [FieldOffset(4)] public uint buffer_size;
}

/// <summary>VAEncMiscParameterFrameRate.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct VAEncMiscParameterFrameRate
{
    /// <summary>A whole number of frames a second, or a fraction with the denominator in the top 16 bits.</summary>
    [FieldOffset(0)] public uint framerate;
    [FieldOffset(4)] public uint framerate_flags;
}

/// <summary>
/// VAEncMiscParameterBufferMaxFrameSize. It opens with a copy of the type the misc header
/// already carries; libva marks that field deprecated, but drivers still read the struct
/// with it in place, so it is here and filled in.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct VAEncMiscParameterBufferMaxFrameSize
{
    [FieldOffset(0)] public uint type; // VAEncMiscParameterType, again
    [FieldOffset(4)] public uint max_frame_size; // in bits
}

/// <summary>VAPictureHEVC: a picture in the decoded picture buffer, seven 32-bit words.</summary>
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct VAPictureHEVC
{
    [FieldOffset(0)] public uint picture_id; // VASurfaceID
    [FieldOffset(4)] public int pic_order_cnt;
    [FieldOffset(8)] public uint flags;

    public static VAPictureHEVC Invalid => new() { picture_id = VaApi.VA_INVALID_ID, flags = VaApi.VA_PICTURE_HEVC_INVALID };
}

/// <summary>Every struct's C size, checked against what the runtime makes of the declarations here.</summary>
internal static unsafe class VaLayout
{
    [Conditional("DEBUG")]
    public static void Check()
    {
        Debug.Assert(sizeof(VAConfigAttrib) == 8);
        Debug.Assert(sizeof(VASurfaceAttrib) == 24);
        Debug.Assert(sizeof(VAImageFormat) == 48);
        Debug.Assert(sizeof(VAImage) == 120);
        Debug.Assert(sizeof(VACodedBufferSegment) == 48);
        Debug.Assert(sizeof(VAEncPackedHeaderParameterBuffer) == 28);
        Debug.Assert(sizeof(VAEncMiscParameterBuffer) == 4);
        Debug.Assert(sizeof(VAEncMiscParameterRateControl) == 60);
        Debug.Assert(sizeof(VAEncMiscParameterHRD) == 24);
        Debug.Assert(sizeof(VAEncMiscParameterFrameRate) == 24);
        Debug.Assert(sizeof(VAEncMiscParameterBufferMaxFrameSize) == 24);
        Debug.Assert(sizeof(VAPictureHEVC) == 28);
        Debug.Assert(sizeof(VAEncSequenceParameterBufferHEVC) == 116);
        Debug.Assert(sizeof(VAEncPictureParameterBufferHEVC) == 576);
        Debug.Assert(sizeof(VAEncSliceParameterBufferHEVC) == 1076);
        Debug.Assert(sizeof(VAEncSequenceParameterBufferAV1) == 88);
        Debug.Assert(sizeof(VAEncPictureParameterBufferAV1) == 1032);
        Debug.Assert(sizeof(VAEncTileGroupBufferAV1) == 20);
    }
}
