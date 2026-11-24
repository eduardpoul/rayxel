using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.PipeWire;

/*
 * The parts of libpipewire-0.3 and of SPA that the capture uses, with every layout and value
 * taken from the 0.3.65 headers (Debian 12) and checked against them with static assertions.
 * The library's ABI has been stable across 0.3.x and into 1.x: structs only ever grow at their
 * end, and pw_stream_events says by its version field how much of it a caller filled in.
 */

/// <summary>The ids SPA and PipeWire give things. From spa/utils/type.h, spa/param/*.h, spa/buffer/*.h.</summary>
internal static class Spa
{
    // enum spa_type
    public const uint TypeNone = 1, TypeId = 3, TypeInt = 4, TypeLong = 5, TypeRectangle = 10, TypeFraction = 11,
                      TypeObject = 15, TypeChoice = 19;

    // object types
    public const uint ObjectFormat = 0x40003, ObjectParamBuffers = 0x40004, ObjectParamMeta = 0x40005;

    // enum spa_param_type
    public const uint ParamEnumFormat = 3, ParamFormat = 4, ParamBuffers = 5, ParamMeta = 6;

    // enum spa_choice_type
    public const uint ChoiceNone = 0, ChoiceRange = 1, ChoiceFlags = 4;

    // enum spa_format
    public const uint FormatMediaType = 1, FormatMediaSubtype = 2;
    public const uint FormatVideoFormat = 0x20001, FormatVideoModifier = 0x20002, FormatVideoSize = 0x20003,
                      FormatVideoFramerate = 0x20004, FormatVideoColorRange = 0x2000c, FormatVideoColorMatrix = 0x2000d;

    public const uint MediaTypeVideo = 2, MediaSubtypeRaw = 1;

    public const uint VideoFormatNv12 = 23;

    // enum spa_video_color_matrix, enum spa_video_color_range
    public const uint ColorMatrixRgb = 1, ColorMatrixFcc = 2, ColorMatrixBt709 = 3, ColorMatrixBt601 = 4,
                      ColorMatrixSmpte240M = 5, ColorMatrixBt2020 = 6;
    public const uint ColorRange0To255 = 1, ColorRange16To235 = 2;

    // enum spa_param_buffers, enum spa_param_meta
    public const uint ParamBuffersDataType = 6;
    public const uint ParamMetaType = 1, ParamMetaSize = 2;

    // enum spa_meta_type, enum spa_data_type
    public const uint MetaHeader = 1;
    public const uint DataMemPtr = 1, DataMemFd = 2;

    public const int ChunkFlagCorrupted = 1;     // SPA_CHUNK_FLAG_CORRUPTED
    public const uint MetaHeaderFlagCorrupted = 2; // SPA_META_HEADER_FLAG_CORRUPTED

    /// <summary>
    /// Private to gamescope (src/pipewire_gamescope.hpp), so in no system header: a Format
    /// property, a rectangle, giving the size the consumer would like. gamescope only ever
    /// scales down to it, keeping the aspect ratio; 0x0 means "whatever the output is".
    /// </summary>
    public const uint GamescopeFormatVideoRequestedSize = 0x70000;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpaPod
{
    public uint Size; // of the body
    public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaBuffer
{
    public uint NMetas, NDatas;
    public SpaMeta* Metas;
    public SpaData* Datas;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaMeta
{
    public uint Type, Size;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpaMetaHeader
{
    public uint Flags, Offset;
    public long Pts, DtsOffset;
    public ulong Seq;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaData
{
    public uint Type, Flags;
    public long Fd;
    public uint MapOffset, MaxSize;
    public void* Data;
    public SpaChunk* Chunk;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpaChunk
{
    public uint Offset, Size;
    public int Stride, Flags;
}

/// <summary>struct pw_buffer. Only its first field is read; it has grown at its end since 0.3.65.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PwBuffer
{
    public SpaBuffer* Buffer;
    public void* UserData;
    public ulong Size, Requested;
}

/// <summary>struct spa_hook: owned by the listener, written by PipeWire. 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaHook
{
    public void* Next, Prev;     // struct spa_list link
    public void* Funcs, Data;    // struct spa_callbacks cb
    public void* Removed, Priv;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaDictItem
{
    public byte* Key, Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SpaDict
{
    public uint Flags, NItems;
    public SpaDictItem* Items;
}

/// <summary>
/// struct pw_stream_events at PW_VERSION_STREAM_EVENTS 2: version, then eleven callbacks of
/// which this program fills in three. 96 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PwStreamEvents
{
    public const uint CurrentVersion = 2;

    public uint Version;
    public void* Destroy;
    public delegate* unmanaged[Cdecl]<nint, int, int, byte*, void> StateChanged;
    public void* ControlInfo;
    public void* IoChanged;
    public delegate* unmanaged[Cdecl]<nint, uint, SpaPod*, void> ParamChanged;
    public void* AddBuffer;
    public void* RemoveBuffer;
    public delegate* unmanaged[Cdecl]<nint, void> Process;
    public void* Drained;
    public void* Command;
    public void* TriggerDone;
}

internal static unsafe partial class Pw
{
    private const string Library = "libpipewire-0.3.so.0";

    // enum pw_stream_state
    public const int StateError = -1, StateUnconnected = 0, StateStreaming = 3;

    // enum pw_stream_flags, enum pw_direction
    public const int FlagAutoconnect = 1 << 0, FlagMapBuffers = 1 << 2;
    public const int DirectionInput = 0;

    public const uint IdAny = 0xffffffff;

    [LibraryImport(Library, EntryPoint = "pw_init")]
    public static partial void Init(int* argc, byte*** argv);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_new", SetLastError = true)]
    public static partial nint ThreadLoopNew(byte* name, SpaDict* props);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_get_loop")]
    public static partial nint ThreadLoopGetLoop(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_start")]
    public static partial int ThreadLoopStart(nint loop);

    /// <summary>Joins the loop's thread: to be called without the lock, and never on that thread.</summary>
    [LibraryImport(Library, EntryPoint = "pw_thread_loop_stop")]
    public static partial void ThreadLoopStop(nint loop);

    /// <summary>The loop's lock is recursive, and its thread holds it while it runs a callback.</summary>
    [LibraryImport(Library, EntryPoint = "pw_thread_loop_lock")]
    public static partial void ThreadLoopLock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_unlock")]
    public static partial void ThreadLoopUnlock(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_thread_loop_destroy")]
    public static partial void ThreadLoopDestroy(nint loop);

    [LibraryImport(Library, EntryPoint = "pw_context_new", SetLastError = true)]
    public static partial nint ContextNew(nint mainLoop, nint props, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_context_destroy")]
    public static partial void ContextDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "pw_context_connect", SetLastError = true)]
    public static partial nint ContextConnect(nint context, nint props, nuint userDataSize);

    [LibraryImport(Library, EntryPoint = "pw_core_disconnect")]
    public static partial int CoreDisconnect(nint core);

    /// <summary>Copies the dictionary; it need only outlive the call.</summary>
    [LibraryImport(Library, EntryPoint = "pw_properties_new_dict")]
    public static partial nint PropertiesNewDict(SpaDict* dict);

    /// <summary>Takes ownership of <paramref name="props"/>, even when it fails.</summary>
    [LibraryImport(Library, EntryPoint = "pw_stream_new", SetLastError = true)]
    public static partial nint StreamNew(nint core, byte* name, nint props);

    [LibraryImport(Library, EntryPoint = "pw_stream_destroy")]
    public static partial void StreamDestroy(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_add_listener")]
    public static partial void StreamAddListener(nint stream, SpaHook* listener, PwStreamEvents* events, nint data);

    /// <summary>The parameters are copied; they need only outlive the call.</summary>
    [LibraryImport(Library, EntryPoint = "pw_stream_connect")]
    public static partial int StreamConnect(nint stream, int direction, uint targetId, int flags, SpaPod** parameters,
                                            uint count);

    [LibraryImport(Library, EntryPoint = "pw_stream_update_params")]
    public static partial int StreamUpdateParams(nint stream, SpaPod** parameters, uint count);

    [LibraryImport(Library, EntryPoint = "pw_stream_get_state")]
    public static partial int StreamGetState(nint stream, byte** error);

    [LibraryImport(Library, EntryPoint = "pw_stream_state_as_string")]
    public static partial byte* StreamStateAsString(int state);

    [LibraryImport(Library, EntryPoint = "pw_stream_set_active")]
    public static partial int StreamSetActive(nint stream, [MarshalAs(UnmanagedType.U1)] bool active);

    [LibraryImport(Library, EntryPoint = "pw_stream_dequeue_buffer")]
    public static partial PwBuffer* StreamDequeueBuffer(nint stream);

    [LibraryImport(Library, EntryPoint = "pw_stream_queue_buffer")]
    public static partial int StreamQueueBuffer(nint stream, PwBuffer* buffer);
}
