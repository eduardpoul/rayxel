using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rayxel.Link;
using Rayxel.Link.Media;
using Rayxel.Send.Linux.PipeWire;

namespace Rayxel.Send.Linux;

/// <summary>
/// The picture of a PipeWire video source - gamescope's, on a Steam Deck - in NV12 through
/// shared memory, on a PipeWire thread loop of its own. Frames, format changes and failures
/// are reported on that loop's thread, which holds the loop's lock while it does: a handler that
/// takes its time (an encoder) holds up <see cref="Pause"/>, <see cref="Dispose"/> and
/// <see cref="Streaming"/> on other threads until it returns, and holds up nothing of PipeWire's
/// own, whose realtime data thread never waits on this one.
/// <para>
/// Stopping a capture is dangerous to the source, and is done as seldom as possible.
/// </para>
/// <para>
/// gamescope (3.16, src/pipewire.cpp) takes a buffer from its PipeWire stream on its compositor
/// thread, once for every frame it draws, with no lock against the thread the stream itself runs
/// on. When the stream leaves its streaming state while a frame is being drawn, the two collide
/// over the stream's buffers, and gamescope aborts in destroy_buffer - taking the whole Steam
/// session down, and the game in it. It happens at the moment the consumer stops, whether it
/// stops by pausing or by disconnecting, and only when a frame happens to be on its way just
/// then: rarely on an idle screen, once in some tens of times under a game drawing 60 frames a
/// second.
/// </para>
/// <para>
/// Nothing done from this side makes that moment safe. Pausing before closing was tried as a
/// cure and is not one: gamescope has since been seen to die on the pause itself. So the owner of
/// a capture should keep it for as long as it can, and the sender does, which leaves the moment
/// to come once, when the program ends.
/// </para>
/// <para>
/// When a capture does have to go: <see cref="Pause"/>, then a wait of
/// <see cref="ICaptureSource.SettleMs"/>, then <see cref="Dispose"/>, so that at least the
/// disconnect finds no frame in flight. The owner does the waiting, on a thread of its own:
/// neither call may be made from inside one of the capture's callbacks. After
/// <see cref="Pause"/> no callback is made again.
/// </para>
/// <para>A capture is started once; one that could not be started, or has been closed, is replaced by a new one.</para>
/// </summary>
public sealed unsafe class PipeWireCapture : ICaptureSource
{
    private static readonly object InitGate = new();

    // Shared by every capture, and kept for as long as the process runs: PipeWire reads it on
    // every event, and initialising PipeWire is done once.
    private static PwStreamEvents* _events;

    private readonly string _source;
    private readonly object _gate = new();
    private GCHandle _self;
    private SpaHook* _listener;
    private nint _loop, _context, _core, _stream;
    private bool _running; // the loop's thread has been started
    private bool _disposed;

    // Told to stop, or never to begin: nothing is reported from here on.
    private volatile bool _paused;

    // The loop thread's managed id, recorded by every callback: how a call can tell that it is
    // being made from inside one.
    private int _loopThread;

    private bool _haveFormat;
    private int _width, _height;
    private long _coalesced;
    private bool _faultSaid;

    /// <param name="source">The PipeWire node to capture: its node name ("gamescope") or its object.serial.</param>
    public PipeWireCapture(string source)
    {
        _source = source;
    }

    public event Action<CaptureFormat>? FormatChanged;
    public event FrameHandler? Frame;
    public event Action<string>? Failed;

    /// <summary>Frames that were already waiting when a newer one came, and were dropped in its favour. A running total.</summary>
    public long Coalesced => Interlocked.Read(ref _coalesced);

    /// <summary>Why the capture last said it cannot go on; empty while it can.</summary>
    public string Error { get; private set; } = "";

    public bool Start(int width, int height, out string why)
    {
        lock (_gate)
        {
            if (_disposed || _paused || _loop != 0)
            {
                why = "a capture is started once, and this one already has been";
                return false;
            }
            try
            {
                if (Open((uint)Math.Max(width, 0), (uint)Math.Max(height, 0), out why)) return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                why = $"libpipewire-0.3 cannot be loaded: {ex.Message}";
            }
            _paused = true;
            Teardown();
            return false;
        }
    }

    /// <summary>
    /// Whether pictures can arrive: connected to the source and not paused. A capture that has
    /// been kept while the source went away and came back is not, and is no use any more.
    /// </summary>
    public bool Streaming
    {
        get
        {
            if (_paused) return false;

            // From inside a callback the loop's lock is already held and the capture cannot be
            // going away, and taking the gate there could deadlock against a Pause on another thread.
            if (_loopThread == Environment.CurrentManagedThreadId)
                return _stream != 0 && Pw.StreamGetState(_stream, null) == Pw.StateStreaming;

            lock (_gate)
            {
                if (_paused || _stream == 0) return false;
                Pw.ThreadLoopLock(_loop);
                try
                {
                    return Pw.StreamGetState(_stream, null) == Pw.StateStreaming;
                }
                finally
                {
                    Pw.ThreadLoopUnlock(_loop);
                }
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            if (_stream == 0) return;

            // Once the lock is ours no callback is running, and every one after this sees the flag.
            // An inactive consumer is what puts the source's node into its paused state.
            Pw.ThreadLoopLock(_loop);
            Pw.StreamSetActive(_stream, false);
            Pw.ThreadLoopUnlock(_loop);
        }
    }

    /// <summary>Disconnects. Not to be called from inside a callback: the loop's thread is stopped and joined here.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_loopThread == Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("a capture cannot be closed from inside one of its own callbacks");
            _disposed = true;
            _paused = true;
            Teardown();
        }
    }

    private static void Initialise()
    {
        lock (InitGate)
        {
            if (_events != null) return;
            Pw.Init(null, null);

            var events = (PwStreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(PwStreamEvents));
            events->Version = PwStreamEvents.CurrentVersion;
            events->StateChanged = &OnStateChanged;
            events->ParamChanged = &OnParamChanged;
            events->Process = &OnProcess;
            _events = events;
        }
    }

    private bool Open(uint width, uint height, out string why)
    {
        Initialise();
        _self = GCHandle.Alloc(this);
        _listener = (SpaHook*)NativeMemory.AllocZeroed((nuint)sizeof(SpaHook));

        fixed (byte* name = "rayxel-capture\0"u8)
            _loop = Pw.ThreadLoopNew(name, null);
        if (_loop == 0)
        {
            why = $"cannot make a PipeWire loop: {LastError()}";
            return false;
        }
        _context = Pw.ContextNew(Pw.ThreadLoopGetLoop(_loop), 0, 0);
        if (_context == 0)
        {
            why = $"cannot connect to PipeWire: {LastError()}";
            return false;
        }
        if (Pw.ThreadLoopStart(_loop) < 0)
        {
            why = "cannot start PipeWire's thread";
            return false;
        }
        _running = true;

        Pw.ThreadLoopLock(_loop);
        try
        {
            if (Connect(width, height, out why)) return true;

            // Whatever the half-made stream says before it is taken down is not news to anyone.
            _paused = true;
            return false;
        }
        finally
        {
            Pw.ThreadLoopUnlock(_loop);
        }
    }

    /// <summary>With the loop's lock held.</summary>
    private bool Connect(uint width, uint height, out string why)
    {
        _core = Pw.ContextConnect(_context, 0, 0);
        if (_core == 0)
        {
            why = $"cannot connect to PipeWire: {LastError()}";
            return false;
        }

        nint props = NewProperties(_source);
        fixed (byte* name = "rayxel-send\0"u8)
            _stream = Pw.StreamNew(_core, name, props);
        if (_stream == 0)
        {
            why = $"cannot create a stream: {LastError()}";
            return false;
        }
        Pw.StreamAddListener(_stream, _listener, _events, GCHandle.ToIntPtr(_self));

        /*
         * What we will take: NV12 through shared memory, at whatever size and rate the source
         * has. No modifier is offered, and that is what selects shared memory - gamescope's
         * DMA-BUF variants make the modifier mandatory and so do not match. BT.709 limited is
         * asked for because that is what an HEVC decoder assumes for HD when the stream says
         * nothing; gamescope's own default is BT.601.
         */
        const int spaceBytes = 1024;
        ulong* space = stackalloc ulong[spaceBytes / 8];
        var b = new PodBuilder(new Span<byte>(space, spaceBytes));
        int format = b.BeginObject(Spa.ObjectFormat, Spa.ParamEnumFormat);
        b.Id(Spa.FormatMediaType, Spa.MediaTypeVideo);
        b.Id(Spa.FormatMediaSubtype, Spa.MediaSubtypeRaw);
        b.Id(Spa.FormatVideoFormat, Spa.VideoFormatNv12);
        b.RectangleRange(Spa.FormatVideoSize, width != 0 ? width : 1280, height != 0 ? height : 800, 1, 1, 16384, 16384);
        b.FractionRange(Spa.FormatVideoFramerate, 0, 1, 0, 1, 1000, 1);
        b.Id(Spa.FormatVideoColorMatrix, Spa.ColorMatrixBt709);
        b.Id(Spa.FormatVideoColorRange, Spa.ColorRange16To235);
        if (width != 0 && height != 0) b.Rectangle(Spa.GamescopeFormatVideoRequestedSize, width, height);
        b.EndObject(format);

        /*
         * The source is named by target.object, which takes a node name or an object.serial and
         * which every session manager honours; the node id stays "any". Frames are delivered on
         * this capture's own loop: one thread hop more than the realtime callback, in exchange
         * for an encoder that may block without stalling PipeWire's data thread.
         */
        var param = (SpaPod*)((byte*)space + format);
        int res = Pw.StreamConnect(_stream, Pw.DirectionInput, Pw.IdAny, Pw.FlagAutoconnect | Pw.FlagMapBuffers, &param, 1);
        if (res < 0)
        {
            why = $"cannot connect the stream: {Marshal.GetPInvokeErrorMessage(-res)}";
            return false;
        }
        why = "";
        return true;
    }

    private static nint NewProperties(string target)
    {
        ReadOnlySpan<string> pairs =
        [
            "media.type", "Video",
            "media.category", "Capture",
            "media.role", "Screen",
            // If the source goes away, stay unlinked rather than be moved to a webcam.
            "node.dont-reconnect", "true",
            "target.object", target,
        ];
        int count = string.IsNullOrEmpty(target) ? pairs.Length / 2 - 1 : pairs.Length / 2;
        var strings = new nint[count * 2];
        SpaDictItem* items = stackalloc SpaDictItem[count];
        try
        {
            for (int i = 0; i < strings.Length; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(pairs[i]);
            for (int i = 0; i < count; i++)
                items[i] = new SpaDictItem { Key = (byte*)strings[2 * i], Value = (byte*)strings[2 * i + 1] };
            var dict = new SpaDict { Flags = 0, NItems = (uint)count, Items = items };
            return Pw.PropertiesNewDict(&dict);
        }
        finally
        {
            foreach (nint s in strings) Marshal.FreeCoTaskMem(s);
        }
    }

    private void Teardown()
    {
        if (_running)
        {
            Pw.ThreadLoopLock(_loop);
            DestroyStream();
            Pw.ThreadLoopUnlock(_loop);
            Pw.ThreadLoopStop(_loop);
            _running = false;
        }
        else
        {
            DestroyStream();
        }
        if (_context != 0) Pw.ContextDestroy(_context);
        if (_loop != 0) Pw.ThreadLoopDestroy(_loop);
        _context = _loop = 0;

        if (_listener != null) NativeMemory.Free(_listener);
        _listener = null;
        if (_self.IsAllocated) _self.Free();
    }

    private void DestroyStream()
    {
        if (_stream != 0) Pw.StreamDestroy(_stream);
        if (_core != 0) Pw.CoreDisconnect(_core);
        _stream = _core = 0;
    }

    private static string LastError() => Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());

    // ---- On the loop's thread, with its lock held ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(nint data, int old, int state, byte* error)
    {
        if (GCHandle.FromIntPtr(data).Target is not PipeWireCapture c) return;
        try
        {
            c.StateChanged(old, state, error);
        }
        catch (Exception ex)
        {
            c.Fault("state", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnParamChanged(nint data, uint id, SpaPod* param)
    {
        if (GCHandle.FromIntPtr(data).Target is not PipeWireCapture c) return;
        try
        {
            c.ParamChanged(id, param);
        }
        catch (Exception ex)
        {
            c.Fault("format", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is not PipeWireCapture c) return;
        try
        {
            c.Process();
        }
        catch (Exception ex)
        {
            c.Fault("frame", ex);
        }
    }

    /// <summary>
    /// An exception must not unwind into PipeWire's C frames, which would end the process, so it
    /// stops here. One handler that throws on every frame would fill the log, so only the first is told.
    /// </summary>
    private void Fault(string what, Exception ex)
    {
        if (_faultSaid) return;
        _faultSaid = true;
        LinkLog.Error($"capture: the {what} handler failed, and further failures are not reported: {ex}");
    }

    /// <summary>The capture cannot go on. The owner is told and decides; it must not close the capture from in here.</summary>
    private void Fail(string reason)
    {
        Error = reason;
        try
        {
            Failed?.Invoke(reason);
        }
        catch (Exception ex)
        {
            Fault("failure", ex);
        }
    }

    private void StateChanged(int old, int state, byte* error)
    {
        _loopThread = Environment.CurrentManagedThreadId;
        if (_paused) return;

        string? text = error != null ? Marshal.PtrToStringUTF8((nint)error) : null;
        LinkLog.Debug($"capture: stream {Marshal.PtrToStringUTF8((nint)Pw.StreamStateAsString(state))}" +
                      (text != null ? $" ({text})" : ""));

        if (state == Pw.StateError) Fail(text ?? "stream error");
        else if (state == Pw.StateUnconnected && old != Pw.StateUnconnected) Fail("disconnected from PipeWire");
    }

    private void ParamChanged(uint id, SpaPod* param)
    {
        _loopThread = Environment.CurrentManagedThreadId;
        if (param == null || id != Spa.ParamFormat || _paused) return;

        // spa_format_parse: a format object, with a media type and subtype.
        if (!PodReader.IsObject(param, Spa.ObjectFormat) ||
            !PodReader.Id(param, Spa.FormatMediaType, out uint mediaType) ||
            !PodReader.Id(param, Spa.FormatMediaSubtype, out uint mediaSubtype))
            return;
        if (mediaType != Spa.MediaTypeVideo || mediaSubtype != Spa.MediaSubtypeRaw) return;

        // spa_format_video_raw_parse: every field optional, and zero when it is not there. Read
        // through the parser's rules, not straight out of the pod: a property that was negotiated
        // is still wrapped in a one-value choice after fixation, and only the parser unwraps it.
        // That goes for gamescope's requested size as well.
        PodReader.Id(param, Spa.FormatVideoFormat, out uint format);
        PodReader.Rectangle(param, Spa.FormatVideoSize, out uint width, out uint height);
        PodReader.Fraction(param, Spa.FormatVideoFramerate, out uint rateNum, out uint rateDen);
        PodReader.Id(param, Spa.FormatVideoColorMatrix, out uint matrix);
        PodReader.Id(param, Spa.FormatVideoColorRange, out uint range);
        PodReader.Rectangle(param, Spa.GamescopeFormatVideoRequestedSize, out uint requestedWidth, out uint requestedHeight);

        // A modifier in the agreed format is how a source says "DMA-BUF".
        bool dmabuf = PodReader.Has(param, Spa.FormatVideoModifier);

        _width = (int)width;
        _height = (int)height;
        _haveFormat = true;

        // A handler that throws must not keep the buffers below from being asked for, or the
        // stream would wait for them for ever.
        try
        {
            FormatChanged?.Invoke(new CaptureFormat(_width, _height, matrix == Spa.ColorMatrixBt601,
                range == Spa.ColorRange0To255,
                Describe(format, width, height, rateNum, rateDen, matrix, range, dmabuf, requestedWidth, requestedHeight)));
        }
        catch (Exception ex)
        {
            Fault("format", ex);
        }

        // The source allocates the buffers and says how large they are. All this side has to
        // state is which kinds of memory it can read and that it wants the frame header.
        const int spaceBytes = 512;
        ulong* space = stackalloc ulong[spaceBytes / 8];
        var b = new PodBuilder(new Span<byte>(space, spaceBytes));
        int buffers = b.BeginObject(Spa.ObjectParamBuffers, Spa.ParamBuffers);
        b.IntFlags(Spa.ParamBuffersDataType, (1 << (int)Spa.DataMemFd) | (1 << (int)Spa.DataMemPtr));
        b.EndObject(buffers);
        int meta = b.BeginObject(Spa.ObjectParamMeta, Spa.ParamMeta);
        b.Id(Spa.ParamMetaType, Spa.MetaHeader);
        b.Int(Spa.ParamMetaSize, sizeof(SpaMetaHeader));
        b.EndObject(meta);

        SpaPod** parameters = stackalloc SpaPod*[2];
        parameters[0] = (SpaPod*)((byte*)space + buffers);
        parameters[1] = (SpaPod*)((byte*)space + meta);
        Pw.StreamUpdateParams(_stream, parameters, 2);
    }

    private void Process()
    {
        _loopThread = Environment.CurrentManagedThreadId;
        PwBuffer* newest = null, next;
        uint coalesced = 0;

        // If more than one frame is waiting we are already behind, and the oldest of them is the
        // least worth sending. Take the newest and hand the rest straight back.
        while ((next = Pw.StreamDequeueBuffer(_stream)) != null)
        {
            if (newest != null)
            {
                Pw.StreamQueueBuffer(_stream, newest);
                coalesced++;
            }
            newest = next;
        }
        if (newest == null) return;

        // One that was already on its way when the capture was paused: straight back.
        if (_paused)
        {
            Pw.StreamQueueBuffer(_stream, newest);
            return;
        }

        long arrival = Clock.NowNs;
        if (coalesced != 0) Interlocked.Add(ref _coalesced, coalesced);
        try
        {
            Deliver(newest->Buffer, arrival);
        }
        finally
        {
            Pw.StreamQueueBuffer(_stream, newest);
        }
    }

    /// <summary>
    /// Hands the buffer's picture over while the buffer is still held. One that carries no
    /// picture is not handed over at all: a buffer that straddles a size change, which the
    /// source flags as corrupted; one that is not mapped (DMA-BUF, which is never asked for); one
    /// whose chunk does not lie inside its memory; and any before the format is known.
    /// </summary>
    private void Deliver(SpaBuffer* buffer, long arrival)
    {
        FrameHandler? handler = Frame;
        if (handler == null || !_haveFormat || buffer->NDatas == 0) return;

        var header = (SpaMetaHeader*)FindMeta(buffer, Spa.MetaHeader, (uint)sizeof(SpaMetaHeader));
        if (header != null && (header->Flags & Spa.MetaHeaderFlagCorrupted) != 0) return;

        SpaData* d = &buffer->Datas[0];
        SpaChunk* chunk = d->Chunk;
        if (chunk == null || (chunk->Flags & Spa.ChunkFlagCorrupted) != 0) return;
        if (d->Data == null || chunk->Offset > d->MaxSize || chunk->Size > d->MaxSize - chunk->Offset) return;

        // NV12 at 8 bits: a source that states no stride packs its rows.
        int stride = chunk->Stride > 0 ? chunk->Stride : _width;
        var frame = new CapturedFrame((byte*)d->Data + chunk->Offset, (int)Math.Min(chunk->Size, int.MaxValue), stride,
            _width, _height, arrival);
        handler(in frame);
    }

    /// <summary>spa_buffer_find_meta_data.</summary>
    private static void* FindMeta(SpaBuffer* buffer, uint type, uint size)
    {
        for (uint i = 0; i < buffer->NMetas; i++)
            if (buffer->Metas[i].Type == type)
                return buffer->Metas[i].Size >= size ? buffer->Metas[i].Data : null;
        return null;
    }

    private static string Describe(uint format, uint width, uint height, uint rateNum, uint rateDen, uint matrix,
                                   uint range, bool dmabuf, uint requestedWidth, uint requestedHeight)
    {
        string text = $"{(format == Spa.VideoFormatNv12 ? "NV12" : $"video format {format}")} {width}x{height}, " +
                      (rateNum == 0 ? "variable rate" : $"{rateNum}/{rateDen} fps") +
                      $", {MatrixName(matrix)} {RangeName(range)}, {(dmabuf ? "DMA-BUF" : "shared memory")}";
        return requestedWidth != 0 ? $"{text}, asked for {requestedWidth}x{requestedHeight}" : text;
    }

    private static string MatrixName(uint matrix) => matrix switch
    {
        Spa.ColorMatrixRgb => "RGB",
        Spa.ColorMatrixFcc => "FCC",
        Spa.ColorMatrixBt709 => "BT.709",
        Spa.ColorMatrixBt601 => "BT.601",
        Spa.ColorMatrixSmpte240M => "SMPTE 240M",
        Spa.ColorMatrixBt2020 => "BT.2020",
        _ => "unspecified"
    };

    private static string RangeName(uint range) => range switch
    {
        Spa.ColorRange0To255 => "full",
        Spa.ColorRange16To235 => "limited",
        _ => "unspecified"
    };
}
