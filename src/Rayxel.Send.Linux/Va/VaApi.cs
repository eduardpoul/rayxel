using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.Va;

/// <summary>
/// The parts of libva (va.h), libva-drm (va_drm.h) and libc that the encoder calls. Every
/// SteamOS has both libraries, as does any desktop with VA-API drivers installed; they are
/// loaded the first time one of these is called, so a machine without them only fails then.
/// <para>
/// The names are libva's own, so that what is called here can be found in its headers. The
/// numbers are its enums' and defines' values as libva 1.17's headers give them, checked by
/// a program compiled against those headers; libva only ever adds to them.
/// </para>
/// </summary>
internal static unsafe partial class VaApi
{
    private const string Va = "libva.so.2";
    private const string VaDrm = "libva-drm.so.2";
    private const string Libc = "libc";

    // VAStatus
    public const int VA_STATUS_SUCCESS = 0;

    // VAGenericID, VASurfaceID
    public const uint VA_INVALID_ID = 0xffffffff;
    public const uint VA_INVALID_SURFACE = VA_INVALID_ID;

    // VAProfile
    public const int VAProfileHEVCMain = 17;
    public const int VAProfileAV1Profile0 = 32;

    // VAEntrypoint
    public const int VAEntrypointEncSlice = 6;
    public const int VAEntrypointEncSliceLP = 8;

    // VAConfigAttribType
    public const int VAConfigAttribRTFormat = 0;
    public const int VAConfigAttribRateControl = 5;
    public const int VAConfigAttribEncPackedHeaders = 10;

    /// <summary>
    /// New in VA-API 1.16. A libva or driver older than that answers it as an attribute it
    /// does not have, which is the same answer as a driver that leaves the choice open.
    /// </summary>
    public const int VAConfigAttribEncAV1Ext2 = 54;

    public const uint VA_ATTRIB_NOT_SUPPORTED = 0x80000000;

    // Values of VAConfigAttribRTFormat, VAConfigAttribRateControl, VAConfigAttribEncPackedHeaders.
    public const uint VA_RT_FORMAT_YUV420 = 0x00000001;
    public const uint VA_RC_CBR = 0x00000002;
    public const uint VA_ENC_PACKED_HEADER_SEQUENCE = 0x00000001;
    public const uint VA_ENC_PACKED_HEADER_PICTURE = 0x00000002;
    public const uint VA_ENC_PACKED_HEADER_SLICE = 0x00000004;

    // VASurfaceAttribType, its flags, VAGenericValueType
    public const int VASurfaceAttribPixelFormat = 1;
    public const uint VA_SURFACE_ATTRIB_SETTABLE = 0x00000002;
    public const int VAGenericValueTypeInteger = 1;

    /// <summary>VA_FOURCC('N', 'V', '1', '2').</summary>
    public const uint VA_FOURCC_NV12 = 0x3231564E;

    /// <summary>vaCreateContext's flag for a progressive picture.</summary>
    public const int VA_PROGRESSIVE = 0x1;

    // VABufferType
    public const int VAEncCodedBufferType = 21;
    public const int VAEncSequenceParameterBufferType = 22;
    public const int VAEncPictureParameterBufferType = 23;
    public const int VAEncSliceParameterBufferType = 24;
    public const int VAEncPackedHeaderParameterBufferType = 25;
    public const int VAEncPackedHeaderDataBufferType = 26;
    public const int VAEncMiscParameterBufferType = 27;

    // VAEncMiscParameterType
    public const uint VAEncMiscParameterTypeFrameRate = 0;
    public const uint VAEncMiscParameterTypeRateControl = 1;
    public const uint VAEncMiscParameterTypeMaxFrameSize = 4;
    public const uint VAEncMiscParameterTypeHRD = 5;

    // VAEncPackedHeaderType
    public const uint VAEncPackedHeaderSequence = 1;
    public const uint VAEncPackedHeaderPicture = 2;
    public const uint VAEncPackedHeaderSlice = 3;

    // VAPictureHEVC.flags
    public const uint VA_PICTURE_HEVC_INVALID = 0x00000001;
    public const uint VA_PICTURE_HEVC_RPS_ST_CURR_BEFORE = 0x00000010;

    // open(2)'s flags, as Linux numbers them on x86-64 and arm64 alike.
    public const int O_RDWR = 0x2;
    public const int O_CLOEXEC = 0x80000;

    [LibraryImport(VaDrm)]
    public static partial IntPtr vaGetDisplayDRM(int fd);

    /// <summary>Takes a <c>void (*)(void *user_context, const char *message)</c>; returns the one it replaces.</summary>
    [LibraryImport(Va)]
    public static partial IntPtr vaSetInfoCallback(IntPtr dpy, IntPtr callback, IntPtr userContext);

    [LibraryImport(Va)]
    public static partial int vaInitialize(IntPtr dpy, int* major, int* minor);

    [LibraryImport(Va)]
    public static partial int vaTerminate(IntPtr dpy);

    [LibraryImport(Va)]
    public static partial byte* vaErrorStr(int status);

    [LibraryImport(Va)]
    public static partial int vaMaxNumEntrypoints(IntPtr dpy);

    [LibraryImport(Va)]
    public static partial int vaQueryConfigEntrypoints(IntPtr dpy, int profile, int* entrypoints, int* count);

    [LibraryImport(Va)]
    public static partial int vaGetConfigAttributes(IntPtr dpy, int profile, int entrypoint, VAConfigAttrib* attribs, int count);

    [LibraryImport(Va)]
    public static partial int vaCreateConfig(IntPtr dpy, int profile, int entrypoint, VAConfigAttrib* attribs, int count,
                                             uint* config);

    [LibraryImport(Va)]
    public static partial int vaDestroyConfig(IntPtr dpy, uint config);

    [LibraryImport(Va)]
    public static partial int vaCreateSurfaces(IntPtr dpy, uint format, uint width, uint height, uint* surfaces, uint count,
                                               VASurfaceAttrib* attribs, uint attribCount);

    [LibraryImport(Va)]
    public static partial int vaDestroySurfaces(IntPtr dpy, uint* surfaces, int count);

    [LibraryImport(Va)]
    public static partial int vaCreateContext(IntPtr dpy, uint config, int width, int height, int flag, uint* targets,
                                              int count, uint* context);

    [LibraryImport(Va)]
    public static partial int vaDestroyContext(IntPtr dpy, uint context);

    [LibraryImport(Va)]
    public static partial int vaCreateBuffer(IntPtr dpy, uint context, int type, uint size, uint count, void* data,
                                             uint* buffer);

    [LibraryImport(Va)]
    public static partial int vaMapBuffer(IntPtr dpy, uint buffer, void** mapped);

    [LibraryImport(Va)]
    public static partial int vaUnmapBuffer(IntPtr dpy, uint buffer);

    [LibraryImport(Va)]
    public static partial int vaDestroyBuffer(IntPtr dpy, uint buffer);

    [LibraryImport(Va)]
    public static partial int vaBeginPicture(IntPtr dpy, uint context, uint target);

    [LibraryImport(Va)]
    public static partial int vaRenderPicture(IntPtr dpy, uint context, uint* buffers, int count);

    [LibraryImport(Va)]
    public static partial int vaEndPicture(IntPtr dpy, uint context);

    [LibraryImport(Va)]
    public static partial int vaSyncSurface(IntPtr dpy, uint surface);

    [LibraryImport(Va)]
    public static partial int vaMaxNumImageFormats(IntPtr dpy);

    [LibraryImport(Va)]
    public static partial int vaQueryImageFormats(IntPtr dpy, VAImageFormat* formats, int* count);

    [LibraryImport(Va)]
    public static partial int vaCreateImage(IntPtr dpy, VAImageFormat* format, int width, int height, VAImage* image);

    [LibraryImport(Va)]
    public static partial int vaDestroyImage(IntPtr dpy, uint image);

    [LibraryImport(Va)]
    public static partial int vaPutImage(IntPtr dpy, uint surface, uint image, int srcX, int srcY, uint srcWidth,
                                         uint srcHeight, int destX, int destY, uint destWidth, uint destHeight);

    [LibraryImport(Va)]
    public static partial int vaDeriveImage(IntPtr dpy, uint surface, VAImage* image);

    /// <summary>
    /// open(2) without a mode, which it reads only when it creates a file. ("libc" is the
    /// one name the runtime maps to the C library itself, libc.so.6.)
    /// </summary>
    [LibraryImport(Libc, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(Libc)]
    public static partial int close(int fd);

    /// <summary>vaErrorStr's text for a status.</summary>
    public static string ErrorString(int status) =>
        Marshal.PtrToStringUTF8((IntPtr)vaErrorStr(status)) ?? $"VA status {status}";
}
