using System.Diagnostics;
using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Rayxel.Render;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>What the pump can honestly say about the video right now.</summary>
public enum CaptureState
{
    /// <summary>No mode selected yet.</summary>
    Idle,

    /// <summary>Mode selected, waiting for the first frame.</summary>
    Starting,

    /// <summary>Frames arriving.</summary>
    Streaming,

    /// <summary>
    /// The device is open and healthy but has stopped producing frames. The console is
    /// asleep, the HDMI cable is out, or the source changed resolution and the card is
    /// relocking. Nothing is wrong with us, and it usually recovers by itself.
    /// </summary>
    NoSignal,

    /// <summary>The device went away, or something else took it.</summary>
    Lost
}

/// <summary>Rolling measurements for the mode currently running.</summary>
public sealed class PumpStats
{
    public long FramesArrived;
    public long FramesDropped;
    /// <summary>Frames whose arrival gap exceeded 1.5x the nominal interval — a stall or a missed capture.</summary>
    public long FramesLate;
    public double MeasuredFps;

    /// <summary>Spread of arrival intervals. Steadiness, NOT delay — a pipeline can be very late and very steady.</summary>
    public double IntervalJitterMs;

    /// <summary>
    /// Age of a frame when we first see it: the gap between the timestamp the capture
    /// stack put on the sample and the moment ReadSample handed it to us. This is the
    /// only figure here that is actually latency, and it is where a codec in the path
    /// should show up, because encode, USB transfer and decode all happen inside it.
    /// </summary>
    public double PipelineDelayMs;
    public bool PipelineDelayValid;

    public long BytesPerFrame;
}

/// <summary>
/// Pulls frames off the device on a dedicated thread and uploads each one straight
/// into the renderer's texture. Runs its own thread because ReadSample blocks until a
/// frame is ready, which would otherwise stall the window's message loop.
/// </summary>
public sealed class FramePump : IDisposable, INetSink
{
    private readonly Renderer _renderer;
    private readonly AutoResetEvent _frameReady = new(false);

    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private IMFDXGIDeviceManager? _dxgiManager;
    private Thread? _thread;
    private volatile bool _running;

    // Used instead of a reader when the device is a sender on the network. The size is the
    // one the surface is built for, packed as width and height, and the pending one is what
    // the network thread saw arrive instead.
    private NetFeed? _net;
    private long _lastAliveTicks;
    private long _netSize, _pendingNetSize;

    // Where in the delay log the frame whose age is still being measured goes.
    private int _deferredDelaySlot = -1;

    /// <summary>
    /// Time a network frame's decode by waiting for the GPU to finish it. That is what makes
    /// the decode figure, and the age that includes it, real rather than the time it took
    /// to hand the frame over — and it costs a brief spin per frame, so it is switched on
    /// only while the figures are being shown or recorded.
    /// </summary>
    public volatile bool MeasureDecode;

    // Rolling window over the last second, so the HUD reflects now rather than an average
    // since startup — a mode switch has to show up immediately to be verifiable.
    private readonly object _statsGate = new();
    private long _lastArrivalTicks;
    private long _startedTicks;
    private double _emaIntervalMs;
    private double _emaJitterMs;
    private long _framesArrived, _framesDropped, _framesLate;
    private double _emaPipelineDelayMs;
    private int _pipelineDelaySamples, _pipelineDelayRejected;

    // Every frame's raw numbers, kept only while something is being measured. The moving
    // averages above are right for a HUD and wrong for a comparison: they hide the tail,
    // and the tail is where a pipeline change shows up first.
    private double[]? _delayLog, _intervalLog;
    private int _logCount;

    /// <summary>Current time on Media Foundation's clock: QPC expressed in 100 ns units.</summary>
    private static long MfSystemTime() =>
        (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    public WaitHandle FrameReady => _frameReady;

    /// <summary>QPC stamp of the most recent frame handed to us, for arrival-to-present timing.</summary>
    public long LastArrivalQpc;
    public CaptureMode? CurrentMode { get; private set; }
    public double CurrentFps { get; private set; }
    public string ActiveSubtype { get; private set; } = "-";
    public bool ConverterInserted { get; private set; }

    private volatile CaptureState _state = CaptureState.Idle;

    /// <summary>Why the pump is in its current state, when there is anything to say.</summary>
    public string? StateDetail { get; private set; }

    /// <summary>
    /// The device was lost because another application is already streaming from it. Kept
    /// apart from <see cref="StateDetail"/>, which is a sentence for the log, so the
    /// interface can say "in use" in the user's language instead of matching on English.
    /// </summary>
    public bool HeldElsewhere => StateDetail == HeldElsewhereDetail;

    private const string HeldElsewhereDetail =
        "another application is already streaming from this device - Windows allows the "
        + "open, but the hardware feeds only one consumer at a time";

    /// <summary>
    /// Current state, with a watchdog folded in: a device that is open and erroring is
    /// visibly broken, but a device that is open and simply silent looks identical to a
    /// working one from the API's side. The only way to tell is to notice frames stopped.
    /// </summary>
    public CaptureState State
    {
        get
        {
            var s = _state;
            if (s is not (CaptureState.Streaming or CaptureState.Starting)) return s;

            long last = Volatile.Read(ref LastArrivalQpc);
            long since = last != 0 ? last : Volatile.Read(ref _startedTicks);
            if (since == 0) return s;

            // A sender on the network says so when it is there with nothing new to show: a
            // paused game produces no frames at all, and that is not a lost signal.
            long alive = Volatile.Read(ref _lastAliveTicks);
            if (alive > since) since = alive;

            double sinceMs = (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
            double frameMs = CurrentFps > 0 ? 1000.0 / CurrentFps : 0;

            // A UVC device takes a few hundred milliseconds to lock and start streaming, so
            // the wait for the very first frame gets a longer grace than the gap between two.
            double allowed = last != 0 ? Math.Max(500, frameMs * 8) : 2000;
            if (sinceMs <= allowed) return last != 0 ? CaptureState.Streaming : CaptureState.Starting;
            return CaptureState.NoSignal;
        }
    }

    /// <summary>The device this pump is currently pointed at.</summary>
    public CaptureDeviceInfo Device { get; private set; }

    public FramePump(CaptureDeviceInfo device, Renderer renderer)
    {
        Device = device;
        _renderer = renderer;
    }

    /// <summary>
    /// Points the pump at a different device, stopping the current one first. The mode list
    /// belongs to the device, so the caller has to pick a new mode before frames resume.
    /// </summary>
    public void SwitchTo(CaptureDeviceInfo device)
    {
        if (device.Is(Device)) return;
        Stop();
        _reader?.Dispose(); _reader = null;
        _source?.Dispose(); _source = null;
        CurrentMode = null;
        CurrentFps = 0;
        ActiveSubtype = "-";
        Device = device;
        Log.Info($"pump now pointed at '{device.FriendlyName}' ({device.ShortId})");
    }

    /// <summary>
    /// Binds our D3D device to Media Foundation so decoded frames land in video memory
    /// as textures instead of being copied through system memory first.
    /// </summary>
    public void AttachD3D(ID3D11Device device)
    {
        // Vortice's wrapper keeps the reset token that MFCreateDXGIDeviceManager hands
        // back, so ResetDevice only needs the device itself.
        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device);
        _dxgiManager = manager;
        Log.Info("DXGI device manager bound to MF — frames will arrive as D3D11 textures");
    }

    /// <summary>
    /// Switches to a mode, logging what was asked for against what Media Foundation
    /// actually accepted. The two differ more often than the API suggests, and silently.
    /// </summary>
    public void Select(CaptureMode mode, double fps)
    {
        Stop();

        if (Device.Bus == DeviceBus.Network)
        {
            SelectNetwork(mode, fps);
            return;
        }

        Log.Section($"switching to #{mode.TypeIndex}: {mode.Width}x{mode.Height} @ {fps:0.##} {mode.Format.Name}");
        Log.Info($"requested : {mode.Width}x{mode.Height} @ {fps:0.##} {mode.Format.Name} " +
                 $"[{DescribeTransport(mode)}]");

        _reader?.Dispose(); _reader = null;
        _source?.Dispose(); _source = null;

        _source = CaptureDevice.OpenSource(Device);
        _reader = CaptureDevice.CreateReader(_source, _dxgiManager);

        using (var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, mode.TypeIndex))
        {
            // Media types carry a rate range; pin it to the rate the user picked.
            native.Set(MfGuids.FrameRate, MfGuids.Pack((uint)Math.Round(fps * 1000), 1000));
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, native);
        }

        ConverterInserted = false;
        using (var actual = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            var subtype = actual.GetGUID(MfGuids.Subtype);
            var format = VideoFormat.FromSubtype(subtype);
            var (w, h) = MfGuids.Unpack(actual.GetUInt64(MfGuids.FrameSize));
            var (num, den) = MfGuids.Unpack(actual.GetUInt64(MfGuids.FrameRate));
            double actualFps = den == 0 ? 0 : num / (double)den;

            ActiveSubtype = format.Name;
            Log.Info($"accepted  : {w}x{h} @ {actualFps:0.##} {format.Name}");

            if (w != mode.Width || h != mode.Height)
                Log.Warn($"resolution differs from request ({mode.Width}x{mode.Height} -> {w}x{h})");
            if (Math.Abs(actualFps - fps) > 0.5)
                Log.Warn($"frame rate differs from request ({fps:0.##} -> {actualFps:0.##})");
            if (format.Name != mode.Format.Name)
            {
                ConverterInserted = true;
                Log.Warn($"subtype differs from request ({mode.Format.Name} -> {format.Name}) — MF inserted a converter");
            }

            bool tenBit = format.IsHighBitDepth;
            if (format.Name is not ("NV12" or "P010"))
            {
                Log.Warn($"{format.Name} has no direct render path in this demo; the picture will be blank. " +
                         "Pick an NV12 or P010 mode to see video.");
            }
            // HDR10 is always ten bits, so an 8-bit mode is SDR whatever else it says.
            _renderer.SourceReportsHdr = tenBit && MfGuids.SaysHdr10(actual);
            if (_renderer.SourceReportsHdr) Log.Info("the device says this mode is HDR10 (PQ, BT.2020)");
            _renderer.ConfigureVideo((int)w, (int)h, tenBit);

            ResetStats();

            CurrentMode = mode;
            CurrentFps = actualFps;
        }

        Volatile.Write(ref LastArrivalQpc, 0);
        Volatile.Write(ref _startedTicks, Stopwatch.GetTimestamp());
        StateDetail = null;
        _state = CaptureState.Starting;

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void ResetStats()
    {
        lock (_statsGate)
        {
            _framesArrived = 0; _framesDropped = 0; _framesLate = 0;
            _emaIntervalMs = 0; _emaJitterMs = 0; _lastArrivalTicks = 0;
            _emaPipelineDelayMs = 0; _pipelineDelaySamples = 0; _pipelineDelayRejected = 0;
        }
    }

    /// <summary>
    /// The network counterpart of <see cref="Select"/>: asks the sender for this size and
    /// starts receiving. "Mode" here means a size to request, and the rate is the one the
    /// bitrate is budgeted for — the frames themselves come when the Deck draws them.
    /// </summary>
    private void SelectNetwork(CaptureMode mode, double fps)
    {
        Log.Section($"network source: {mode.Width}x{mode.Height} @ {fps:0.##} {NetDevice.CodecName(NetDevice.Codec)} " +
                    $"from {Device.Hardware.Instance}");

        if (_dxgiManager is null)
            throw new InvalidOperationException("the decoder needs the Direct3D device attached first");
        if (!NetDevice.TryGetEndPoint(Device, out var endPoint, out string problem))
        {
            Fail(CaptureState.Lost, problem);
            return;
        }

        // What the decoder produces, whatever is on the wire.
        ActiveSubtype = "NV12";
        ConverterInserted = false;
        _renderer.SourceReportsHdr = false;
        _renderer.ConfigureVideo(mode.Width, mode.Height, false);
        Volatile.Write(ref _netSize, PackSize(mode.Width, mode.Height));
        Volatile.Write(ref _pendingNetSize, 0);
        ResetStats();

        CurrentMode = mode;
        CurrentFps = fps;
        Volatile.Write(ref LastArrivalQpc, 0);
        Volatile.Write(ref _lastAliveTicks, 0);
        Volatile.Write(ref _startedTicks, Stopwatch.GetTimestamp());
        StateDetail = null;
        _state = CaptureState.Starting;

        _running = true;
        _net = new NetFeed(endPoint, NetDevice.PeerOf(Device), mode.Width, mode.Height, (int)Math.Round(fps), _dxgiManager,
                           _renderer.Device, this, () => _hasFocus);
        _net.Start();
    }

    private static long PackSize(int width, int height) => ((long)width << 32) | (uint)height;

    private volatile bool _hasFocus;

    /// <summary>
    /// Whether this app's window is the one the user is working in. Set every frame by the
    /// thread that owns the window; a network source forwards the controller only while
    /// it is true.
    /// </summary>
    public bool HasFocus { get => _hasFocus; set => _hasFocus = value; }

    /// <summary>
    /// True while a network source has the keyboard and mouse: they are then the Deck's, and
    /// the window must leave them alone.
    /// </summary>
    public bool InputCaptured => _net?.InputCaptured == true;

    bool INetSink.NetPicture(ID3D11Texture2D texture, int subresource, int width, int height, long captureTime)
    {
        if (!_running) return false;
        _state = CaptureState.Streaming;

        // The sender may deliver another size than was asked for: gamescope fits the request
        // into its own picture and never scales up. Rebuilding the surface belongs to the
        // thread that owns the renderer, so the size is left for it and this frame is not
        // shown; the next one finds a surface that fits.
        long size = PackSize(width, height);
        bool timed = false;
        if (size != Volatile.Read(ref _netSize)) Volatile.Write(ref _pendingNetSize, size);
        else timed = _renderer.UploadFrame(texture, subresource, width, height, mark: MeasureDecode);

        // The frame loop is woken before the wait, not after it: its drawing goes into the
        // GPU's queue behind the decode either way, and holding it back until the decode is
        // finished would add the wake-up to every frame for the sake of a measurement.
        RecordArrival(captureTime, deferDelay: timed);
        _frameReady.Set();

        if (!timed) return false;
        if (!_renderer.WaitForUpload(20)) return false;
        RecordDeferredDelay(captureTime);
        return true;
    }

    void INetSink.NetLost(long frames) => Interlocked.Add(ref _framesDropped, frames);

    void INetSink.NetAlive() => Volatile.Write(ref _lastAliveTicks, Stopwatch.GetTimestamp());

    void INetSink.NetFailed(string detail) => Fail(CaptureState.Lost, detail);

    /// <summary>
    /// Does what the network thread could only ask for. Called every frame from the thread
    /// that owns the renderer; a no-op for a capture card.
    /// </summary>
    public void Service()
    {
        // The codec the frames say they are, which is what the mode is said to carry: the
        // one asked for, or HEVC from a Deck that cannot encode it.
        if (_net is { ArrivingCodec: var codec } && codec != "-" && CurrentMode is { } shown && shown.WireCodec != codec)
            CurrentMode = shown with { WireCodec = codec };

        long size = Interlocked.Exchange(ref _pendingNetSize, 0);
        if (size == 0 || size == Volatile.Read(ref _netSize)) return;

        int width = (int)(size >> 32), height = (int)(size & 0xFFFFFFFF);
        _renderer.ConfigureVideo(width, height, false);
        Volatile.Write(ref _netSize, size);
        if (CurrentMode is { } mode) CurrentMode = mode with { Width = width, Height = height };
        Log.Info($"the sender's picture is {width}x{height}, not the size asked for; showing it as it is");
    }

    /// <summary>Where each frame's time went between the Deck and here; null for a capture card.</summary>
    public NetStats? Network => _net?.Snapshot();

    /// <summary>The per-frame figures recorded since <see cref="StartRecording"/>; null for a capture card.</summary>
    public NetRecording? NetworkRecording() => _net?.Recording();

    public static string DescribeTransport(CaptureMode m) => m.Transport switch
    {
        WireTransport.Raw => "raw on the wire",
        WireTransport.CompressedDecoded when m.WireCodec is { } c => $"{c} on the wire, decoded for us",
        WireTransport.CompressedDecoded => "over the link budget - a codec is in the path",
        _ => m.WireCodec ?? m.Format.Name
    };

    private void CaptureLoop()
    {
        Log.Debug("capture thread started");
        int consecutiveErrors = 0;

        while (_running)
        {
            IMFSample? sample = null;
            try
            {
                sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out var flags, out long sampleTime);

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    Fail(CaptureState.Lost, "the device ended the stream");
                    break;
                }
                // A read that returns nothing is normal: the source had no frame ready. The
                // watchdog on State is what notices when that stops being momentary.
                if (sample is null) continue;

                consecutiveErrors = 0;
                _state = CaptureState.Streaming;
                RecordArrival(sampleTime);
                Deliver(sample);
                _frameReady.Set();
            }
            catch (SharpGenException ex)
            {
                if (!_running) break;

                if (FatalReadFailure(ex.ResultCode.Code) is { } reason)
                {
                    Fail(CaptureState.Lost, reason);
                    break;
                }

                // Retry, but not forever: a source erroring on every read will never recover
                // on its own, and a thread burning a core on it helps nobody.
                Log.Error($"ReadSample failed: {ex.ResultCode}");
                if (++consecutiveErrors >= 20)
                {
                    Fail(CaptureState.Lost, $"20 consecutive read failures ({ex.ResultCode})");
                    break;
                }
                Thread.Sleep(50);
            }
            finally { sample?.Dispose(); }
        }
        Log.Debug("capture thread stopped");
    }

    private void Fail(CaptureState state, string detail)
    {
        StateDetail = detail;
        _state = state;
        Log.Warn($"capture stopped: {detail}");
        _frameReady.Set(); // release anyone waiting on a frame that is never coming
    }

    /// <summary>
    /// Read failures that will not fix themselves on retry, and what to tell the user.
    /// Null means "worth trying again". Any code not named here gets the retry path and
    /// then the failure counter, so nothing is silently mistaken for something else.
    /// </summary>
    private static string? FatalReadFailure(int hr) => hr switch
    {
        // MF_E_HW_MFT_FAILED_START_STREAMING. This is what a device already in use looks
        // like: opening it succeeds, setting a media type succeeds, and only the first
        // read says no. Retrying cannot help while the other application is still running.
        unchecked((int)0xC00D3704) => HeldElsewhereDetail,

        // HRESULT_FROM_WIN32(ERROR_GEN_FAILURE).
        unchecked((int)0x8007001F) => "the device stopped responding - most likely unplugged",

        // HRESULT_FROM_WIN32(RPC_S_SERVER_UNAVAILABLE): the Windows frame server went away.
        unchecked((int)0x800706BA) => "the Windows camera frame server stopped",

        _ => null
    };

    private void Deliver(IMFSample sample)
    {
        if (sample.BufferCount == 0) return;
        using var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is null)
        {
            Interlocked.Increment(ref _framesDropped);
            return; // system-memory sample: no D3D path configured
        }

        var ptr = dxgi.GetResource(MfGuids.ID3D11Texture2D);
        if (ptr == IntPtr.Zero) { Interlocked.Increment(ref _framesDropped); return; }

        using var texture = new ID3D11Texture2D(ptr);
        _renderer.UploadFrame(texture, (int)dxgi.SubresourceIndex);
    }

    /// <summary>
    /// Starts keeping every frame's delay and arrival interval, for a measurement that
    /// needs percentiles. Off unless asked for: a comparison is the only thing it is good
    /// for, and it costs two doubles a frame.
    /// </summary>
    public void StartRecording(int capacity)
    {
        lock (_statsGate)
        {
            _delayLog = new double[capacity];
            _intervalLog = new double[capacity];
            _logCount = 0;
            _deferredDelaySlot = -1;
        }
        _net?.StartRecording(capacity);
    }

    /// <summary>Recorded frames so far. NaN entries are frames with nothing trustworthy to say.</summary>
    public (double[] Delays, double[] Intervals) Recording()
    {
        lock (_statsGate)
        {
            if (_delayLog is null || _intervalLog is null) return ([], []);
            return (_delayLog[.._logCount], _intervalLog[.._logCount]);
        }
    }

    private void RecordArrival(long sampleTime100Ns, bool deferDelay = false)
    {
        long now = Stopwatch.GetTimestamp();
        long mfNow = MfSystemTime();
        double nominalMs = CurrentFps > 0 ? 1000.0 / CurrentFps : 0;
        Volatile.Write(ref LastArrivalQpc, now);

        lock (_statsGate)
        {
            double intervalMs = double.NaN, delaySample = double.NaN;

            _framesArrived++;
            if (_lastArrivalTicks != 0)
            {
                double ms = (now - _lastArrivalTicks) * 1000.0 / Stopwatch.Frequency;
                intervalMs = ms;
                // Exponential moving average: cheap, allocation-free, and reacts fast
                // enough that a mode switch is visible within a few frames.
                _emaIntervalMs = _emaIntervalMs == 0 ? ms : _emaIntervalMs * 0.9 + ms * 0.1;
                _emaJitterMs = _emaJitterMs * 0.9 + Math.Abs(ms - _emaIntervalMs) * 0.1;
                if (nominalMs > 0 && ms > nominalMs * 1.5) _framesLate++;
            }
            _lastArrivalTicks = now;

            // The age is taken here unless the caller will take it later, when the frame is
            // really ready; the log keeps its place for that reading.
            if (!deferDelay) delaySample = NoteDelay(sampleTime100Ns, mfNow);

            _deferredDelaySlot = -1;
            if (_delayLog is not null && _logCount < _delayLog.Length)
            {
                if (deferDelay) _deferredDelaySlot = _logCount;
                _delayLog[_logCount] = delaySample;
                _intervalLog![_logCount] = intervalMs;
                _logCount++;
            }
        }
    }

    /// <summary>
    /// One reading of a frame's age, taken against <paramref name="mfNow"/>. NaN when it
    /// cannot be trusted. The caller holds the stats lock.
    /// </summary>
    private double NoteDelay(long sampleTime100Ns, long mfNow)
    {
        // Live capture sources are supposed to stamp samples on the system clock, but
        // that is not guaranteed — some stamp a device clock, or start from zero. A
        // reading is only trusted when it lands in a physically plausible window;
        // otherwise the timestamps are on a different clock and the figure is junk.
        double delayMs = sampleTime100Ns > 0 ? (mfNow - sampleTime100Ns) / 10_000.0 : double.NaN;
        if (delayMs is not (> 0 and < 500))
        {
            _pipelineDelayRejected++;
            return double.NaN;
        }

        _emaPipelineDelayMs = _emaPipelineDelayMs == 0
            ? delayMs
            : _emaPipelineDelayMs * 0.9 + delayMs * 0.1;
        _pipelineDelaySamples++;
        return delayMs;
    }

    /// <summary>
    /// The age of the frame last recorded with its delay deferred, taken now: for a network
    /// frame, the moment the GPU has finished decoding it.
    /// </summary>
    private void RecordDeferredDelay(long sampleTime100Ns)
    {
        long mfNow = MfSystemTime();
        lock (_statsGate)
        {
            double delay = NoteDelay(sampleTime100Ns, mfNow);
            if (_deferredDelaySlot >= 0 && _delayLog is not null && _deferredDelaySlot < _delayLog.Length)
                _delayLog[_deferredDelaySlot] = delay;
            _deferredDelaySlot = -1;
        }
    }

    public PumpStats Snapshot()
    {
        lock (_statsGate)
        {
            return new PumpStats
            {
                FramesArrived = _framesArrived,
                FramesDropped = _framesDropped,
                FramesLate = _framesLate,
                MeasuredFps = _emaIntervalMs > 0 ? 1000.0 / _emaIntervalMs : 0,
                IntervalJitterMs = _emaJitterMs,
                PipelineDelayMs = _emaPipelineDelayMs,
                // Trust the figure only if the clocks clearly agree for most frames.
                PipelineDelayValid = _pipelineDelaySamples > 30
                                     && _pipelineDelaySamples > _pipelineDelayRejected * 4,
                BytesPerFrame = CurrentMode is { } m && m.Transport == WireTransport.Raw
                    ? m.Format.BytesPerFrame(m.Width, m.Height) : 0
            };
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(500);
        _thread = null;
        _net?.Dispose();
        _net = null;
        // Keep a Lost state: it explains why the video went away. A clean stop is just idle.
        if (_state != CaptureState.Lost) _state = CaptureState.Idle;
    }

    public void Dispose()
    {
        Stop();
        _reader?.Dispose();
        _source?.Dispose();
        _dxgiManager?.Dispose();
        _frameReady.Dispose();
    }
}
