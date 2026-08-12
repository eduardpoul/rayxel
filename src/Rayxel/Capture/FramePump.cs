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
public sealed class FramePump : IDisposable
{
    private readonly Renderer _renderer;
    private readonly AutoResetEvent _frameReady = new(false);

    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private IMFDXGIDeviceManager? _dxgiManager;
    private Thread? _thread;
    private volatile bool _running;

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
            _renderer.ConfigureVideo((int)w, (int)h, tenBit);

            lock (_statsGate)
            {
                _framesArrived = 0; _framesDropped = 0; _framesLate = 0;
                _emaIntervalMs = 0; _emaJitterMs = 0; _lastArrivalTicks = 0;
                _emaPipelineDelayMs = 0; _pipelineDelaySamples = 0; _pipelineDelayRejected = 0;
            }

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
        unchecked((int)0xC00D3704) =>
            "another application is already streaming from this device - Windows allows the "
            + "open, but the hardware feeds only one consumer at a time",

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
        }
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

    private void RecordArrival(long sampleTime100Ns)
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

            // Live capture sources are supposed to stamp samples on the system clock, but
            // that is not guaranteed — some stamp a device clock, or start from zero. A
            // reading is only trusted when it lands in a physically plausible window;
            // otherwise the timestamps are on a different clock and the figure is junk.
            if (sampleTime100Ns > 0)
            {
                double delayMs = (mfNow - sampleTime100Ns) / 10_000.0;
                if (delayMs is > 0 and < 500)
                {
                    delaySample = delayMs;
                    _emaPipelineDelayMs = _emaPipelineDelayMs == 0
                        ? delayMs
                        : _emaPipelineDelayMs * 0.9 + delayMs * 0.1;
                    _pipelineDelaySamples++;
                }
                else _pipelineDelayRejected++;
            }
            else _pipelineDelayRejected++;

            if (_delayLog is not null && _logCount < _delayLog.Length)
            {
                _delayLog[_logCount] = delaySample;
                _intervalLog![_logCount] = intervalMs;
                _logCount++;
            }
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
