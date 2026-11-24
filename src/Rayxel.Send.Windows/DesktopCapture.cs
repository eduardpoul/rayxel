using Rayxel.Link;
using Rayxel.Link.Media;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11MapFlags = Vortice.Direct3D11.MapFlags;
using D3D11Rotation = Vortice.Direct3D11.VideoProcessorRotation;

namespace Rayxel.Send.Windows;

/// <summary>
/// A display's picture, by DXGI desktop duplication, in NV12.
/// <para>
/// Duplication hands over the desktop as DWM composed it, as a BGRA texture on a device of
/// ours, and only when something on it changed: an idle desktop sends nothing, much as
/// gamescope draws only when something moves. Each picture goes through the D3D11 video
/// processor, which converts it to NV12 (BT.709, limited range) and scales it to the size
/// asked for in the same pass, and is then copied into a staging texture and read back.
/// </para>
/// <para>
/// The read-back is a copy of every frame through system memory - at 1920x1080 three
/// megabytes, well under a millisecond - and the picture is copied once more into the
/// encoder's input. It is the price of the contract every capture keeps (a picture in CPU
/// memory with a stride): any encoder can take it, PyroWave's Vulkan one as much as Media
/// Foundation's, without each having to share textures with this device in its own way.
/// The conversion and the scaling, which are the expensive part, stay on the GPU.
/// </para>
/// <para>
/// The pointer is not in the picture: duplication reports it beside each frame, and this
/// passes it on to <see cref="DesktopCursor"/> through the platform's <see cref="PointerFeed"/>.
/// When only the pointer moved the last picture is handed over again, no more often than
/// the display refreshes, so that a still desktop still shows the pointer moving.
/// </para>
/// <para>
/// Duplication is lost now and then by design: a mode change, a full-screen game taking the
/// display, the UAC prompt or the lock screen (which live on a desktop no ordinary program
/// may see). Then it is made again, on the same display found afresh; the size may have
/// changed meanwhile, which is a <see cref="FormatChanged"/>. Only a display that stays gone,
/// or a GPU that will not come back, ends in <see cref="Failed"/>. All of this runs on the
/// capture's own thread, which owns every D3D11 object here.
/// </para>
/// </summary>
public sealed unsafe class DesktopCapture : ICaptureSource
{
    /// <summary>
    /// Staging textures read back in turn. The one holding the newest picture is left alone
    /// until the next one is whole in another: it is what a pointer-only update hands over
    /// again, and the next copy never has to wait for the CPU to have finished reading.
    /// </summary>
    private const int StagingCount = 2;

    /// <summary>How long one wait for a frame lasts, so that stopping is noticed.</summary>
    private const uint AcquireTimeoutMs = 100;

    /// <summary>How long the display may be unreachable before the capture gives up.</summary>
    private const long OutageLimitNs = 10_000_000_000;

    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int DxgiAccessDenied = unchecked((int)0x887A002B);
    private const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    private const int SessionDisconnected = unchecked((int)0x887A0028);
    private const int Unsupported = unchecked((int)0x887A0004);
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceHung = unchecked((int)0x887A0006);
    private const int DeviceReset = unchecked((int)0x887A0007);

    private readonly string _source;
    private readonly PointerFeed _pointer;

    // Everything handed to a subscriber goes out under this, after a look at _paused, so
    // that Pause, once it returns, is final.
    private readonly object _deliver = new();
    private volatile bool _paused, _stop, _connected;

    private Thread? _thread;
    private readonly ManualResetEventSlim _opened = new();
    private bool _openOk;
    private string _openProblem = "";
    private int _requestWidth, _requestHeight;

    // The display, and the device on its adapter.
    private IDXGIFactory1? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _video;
    private ID3D11VideoContext? _videoContext;
    private long _deviceLuid;
    private IDXGIOutputDuplication? _duplication;
    private string _label = "";
    private long _refreshNs = 1_000_000_000 / 60;

    // The conversion, built for one desktop image and one output size.
    private ID3D11Texture2D? _desktop;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _nv12;
    private ID3D11VideoProcessorOutputView? _outputView;
    private VideoProcessorStream[] _streams = [];
    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[StagingCount];
    private int _latest = -1;
    private int _imageWidth, _imageHeight;
    private Format _imageFormat;
    private ModeRotation _rotation;
    private int _sourceWidth, _sourceHeight, _width, _height;
    private int _announcedWidth, _announcedHeight;

    // The pointer as last reported.
    private bool _pointerVisible;
    private int _pointerX, _pointerY;
    private PointerShape? _pointerShape;
    private byte[] _shapeBuffer = new byte[64 * 64 * 4];

    public DesktopCapture(string source) : this(source, new PointerFeed())
    {
    }

    internal DesktopCapture(string source, PointerFeed pointer)
    {
        _source = string.IsNullOrWhiteSpace(source) ? "0" : source.Trim();
        _pointer = pointer;
    }

    public event Action<CaptureFormat>? FormatChanged;
    public event FrameHandler? Frame;
    public event Action<string>? Failed;

    public bool Streaming => _connected && !_paused;

    public bool Start(int width, int height, out string why)
    {
        if (_thread is not null)
        {
            why = "this capture has been started already";
            return false;
        }
        _requestWidth = Math.Max(0, width);
        _requestHeight = Math.Max(0, height);

        // The display is opened on the capture's thread, as everything else is, and Start
        // waits to hear how that went.
        _thread = new Thread(Run) { Name = "desktop capture", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_opened.Wait(TimeSpan.FromSeconds(10)))
        {
            _stop = true;
            why = $"display {_source} did not open within 10 s";
            return false;
        }
        why = _openOk ? "" : _openProblem;
        return _openOk;
    }

    public void Pause()
    {
        lock (_deliver) _paused = true;
        _stop = true;
    }

    public void Dispose()
    {
        Pause();
        var thread = _thread;
        if (thread is null || thread == Thread.CurrentThread) return;
        if (thread.Join(TimeSpan.FromSeconds(3))) _opened.Dispose();
        else LinkLog.Warn("capture: the capture's thread did not stop within 3 s; its GPU objects are left to the process's end");
    }

    // ----------------------------------------------------------------------------------
    // The capture's thread.

    private void Run()
    {
        Native.SetThreadDpiAwarenessContext(Native.PerMonitorAwareV2);
        try
        {
            _pointer.Claim(this);
            var opened = Open(out string why);
            _openOk = opened == OpenResult.Ok;
            _openProblem = _openOk ? "" : why;
            _opened.Set();
            if (!_openOk) return;

            _connected = true;
            LinkLog.Info($"capture: {_label}, {Describe()}");
            Announce();
            Loop();
        }
        catch (Exception ex)
        {
            if (!_opened.IsSet)
            {
                _openOk = false;
                _openProblem = ex.Message;
                _opened.Set();
            }
            else
            {
                Fail($"the desktop capture stopped: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            _connected = false;
            _pointer.Leave(this);
            ReleaseAll();
        }
    }

    private void Loop()
    {
        long lastHandedNs = 0, lostSinceNs = 0;
        bool pointerOwed = false, saidSecure = false;

        while (!_stop)
        {
            if (_duplication is null)
            {
                lostSinceNs = lostSinceNs == 0 ? Clock.NowNs : lostSinceNs;
                var opened = Open(out string why);
                if (opened == OpenResult.Ok)
                {
                    LinkLog.Info($"capture: back, {_label}, {Describe()}");
                    lostSinceNs = 0;
                    saidSecure = false;
                    pointerOwed = false;
                    Announce();
                    continue;
                }
                if (opened == OpenResult.Secure)
                {
                    // The UAC prompt or the lock screen. They go away when the user deals
                    // with them, however long that takes, so this is waited out and not
                    // counted against the display.
                    if (!saidSecure) LinkLog.Info($"capture: {why}; waiting for the desktop to come back");
                    saidSecure = true;
                    lostSinceNs = Clock.NowNs;
                    Thread.Sleep(250);
                    continue;
                }
                if (Clock.NowNs - lostSinceNs > OutageLimitNs)
                {
                    Fail($"display {_source}: {why}");
                    return;
                }
                Thread.Sleep(200);
                continue;
            }

            uint timeout = AcquireTimeoutMs;
            if (pointerOwed)
            {
                long left = lastHandedNs + _refreshNs - Clock.NowNs;
                timeout = (uint)Math.Clamp(left / 1_000_000, 0, AcquireTimeoutMs);
            }

            Result result = _duplication.AcquireNextFrame(timeout, out OutduplFrameInfo info, out IDXGIResource? resource);
            long arrivalNs = Clock.NowNs;

            if (result.Code == WaitTimeout)
            {
                resource?.Dispose();
                if (pointerOwed && arrivalNs - lastHandedNs >= _refreshNs && _latest >= 0)
                {
                    Hand(_latest, arrivalNs);
                    lastHandedNs = arrivalNs;
                    pointerOwed = false;
                }
                continue;
            }
            if (result.Failure)
            {
                resource?.Dispose();
                Lose(result.Code, "waiting for a frame");
                continue;
            }

            bool picture = info.LastPresentTime != 0, pointerMoved = false;
            int lost = 0;
            string? fatal = null;
            try
            {
                pointerMoved = ReadPointer(info);

                // The image is only the duplication's until ReleaseFrame. It is copied out at
                // once - the copy is queued on our device ahead of the release - and the frame
                // is given back straight away, so that DWM is not kept waiting on us.
                if (picture && resource is not null)
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    var description = texture.Description;
                    if ((int)description.Width != _imageWidth || (int)description.Height != _imageHeight ||
                        description.Format != _imageFormat)
                        Build((int)description.Width, (int)description.Height, description.Format, _rotation);
                    _context!.CopyResource(_desktop!, texture);
                }
            }
            catch (SharpGenException ex)
            {
                lost = ex.ResultCode.Code;
            }
            catch (InvalidOperationException ex)
            {
                fatal = ex.Message;
            }
            finally
            {
                resource?.Dispose();
                Result released = _duplication.ReleaseFrame();
                if (released.Failure && lost == 0) lost = released.Code;
            }

            if (fatal is not null)
            {
                Fail($"display {_source}: {fatal}");
                return;
            }
            if (lost != 0)
            {
                Lose(lost, "taking a frame");
                continue;
            }
            Announce();

            if (picture)
            {
                int slot = Convert();
                if (slot < 0) continue;
                Hand(slot, arrivalNs);
                lastHandedNs = arrivalNs;
                pointerOwed = false;
            }
            else if (pointerMoved && _latest >= 0)
            {
                if (arrivalNs - lastHandedNs >= _refreshNs)
                {
                    Hand(_latest, arrivalNs);
                    lastHandedNs = arrivalNs;
                    pointerOwed = false;
                }
                else
                {
                    pointerOwed = true;
                }
            }
        }
    }

    /// <summary>
    /// The pointer's news in this frame, passed on to the feed. True when it moved, showed,
    /// hid or changed shape - anything that changes the picture once it is drawn in.
    /// </summary>
    private bool ReadPointer(in OutduplFrameInfo info)
    {
        // A zero time says the pointer has not been touched since the last frame, and its
        // position in this one means nothing.
        if (info.LastMouseUpdateTime == 0) return false;

        bool visible = info.PointerPosition.Visible;
        int x = info.PointerPosition.Position.X, y = info.PointerPosition.Position.Y;
        bool changed = visible != _pointerVisible || (visible && (x != _pointerX || y != _pointerY));

        if (info.PointerShapeBufferSize > 0)
        {
            if (_shapeBuffer.Length < info.PointerShapeBufferSize) _shapeBuffer = new byte[info.PointerShapeBufferSize];
            fixed (byte* buffer = _shapeBuffer)
            {
                Result got = _duplication!.GetFramePointerShape((uint)_shapeBuffer.Length, (IntPtr)buffer, out uint used,
                                                                out OutduplPointerShapeInfo shape);
                if (got.Success && shape.Width > 0 && shape.Height > 0)
                {
                    var type = (PointerShapeType)shape.Type;
                    int height = (int)(type == PointerShapeType.Monochrome ? shape.Height / 2 : shape.Height);
                    _pointerShape = new PointerShape(type, (int)shape.Width, height, (int)shape.Pitch,
                                                     _shapeBuffer.AsSpan(0, (int)Math.Min(used, (uint)_shapeBuffer.Length)).ToArray());
                    changed = true;
                }
            }
        }

        _pointerVisible = visible;
        _pointerX = x;
        _pointerY = y;
        _pointer.Publish(this, new PointerFeed.State(visible, x, y, _sourceWidth, _sourceHeight, _pointerShape));
        return changed;
    }

    /// <summary>
    /// Runs the newest desktop image through the video processor into NV12 and copies that
    /// into the next staging texture. The staging texture it went into, or -1 when the GPU
    /// went away under it.
    /// </summary>
    private int Convert()
    {
        int slot = (_latest + 1) % StagingCount;
        try
        {
            Result blt = _videoContext!.VideoProcessorBlt(_processor!, _outputView!, 0, 1, _streams);
            if (blt.Failure)
            {
                Lose(blt.Code, "converting a frame");
                return -1;
            }
            _context!.CopyResource(_staging[slot]!, _nv12!);
            _latest = slot;
            return slot;
        }
        catch (SharpGenException ex)
        {
            Lose(ex.ResultCode.Code, "converting a frame");
            return -1;
        }
    }

    /// <summary>
    /// Hands the picture in a staging texture to the subscriber. Mapping it is what waits for
    /// the GPU to have finished the conversion and the copy.
    /// </summary>
    private void Hand(int slot, long arrivalNs)
    {
        lock (_deliver)
        {
            if (_paused) return;
            var context = _context!;
            var staging = _staging[slot]!;
            MappedSubresource mapped;
            try
            {
                mapped = context.Map(staging, 0, MapMode.Read, D3D11MapFlags.None);
            }
            catch (SharpGenException ex)
            {
                Lose(ex.ResultCode.Code, "reading a frame back");
                return;
            }

            try
            {
                int stride = (int)mapped.RowPitch;
                var frame = new CapturedFrame((byte*)mapped.DataPointer, stride * _height + stride * (_height / 2), stride,
                                              _width, _height, arrivalNs);
                _pointer.ShowFrame(this, frame);
                try
                {
                    Frame?.Invoke(in frame);
                }
                catch (Exception ex)
                {
                    // Whoever took the frame failed, not the capture; it goes on.
                    LinkLog.Error($"capture: the frame's receiver failed: {ex}");
                }
                finally
                {
                    _pointer.HideFrame(this);
                }
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }

    /// <summary>
    /// The duplication has gone, or the GPU has. Both are made again by the loop; the
    /// device only when it is the device that went.
    /// </summary>
    private void Lose(int code, string what)
    {
        if (_duplication is null) return;

        bool gpu = code is DeviceRemoved or DeviceHung or DeviceReset ||
                   (_device is not null && _device.DeviceRemovedReason.Failure);
        if (code == AccessLost)
            LinkLog.Info("capture: the display changed under the duplication (a mode change, a full-screen switch, " +
                         "the UAC prompt or the lock screen); making it again");
        else
            LinkLog.Warn($"capture: {what} failed ({Hex(code)}){(gpu ? ", the GPU was reset" : "")}; starting again");

        _duplication.Dispose();
        _duplication = null;
        _pointerVisible = false;
        _pointer.Publish(this, new PointerFeed.State(false, 0, 0, _sourceWidth, _sourceHeight, _pointerShape));
        if (gpu) ReleaseDevice();
    }

    private void Fail(string why)
    {
        LinkLog.Error($"capture: {why}");
        _connected = false;
        lock (_deliver)
        {
            if (_paused) return;
            Failed?.Invoke(why);
        }
    }

    private void Announce()
    {
        if (_width == _announcedWidth && _height == _announcedHeight) return;
        _announcedWidth = _width;
        _announcedHeight = _height;
        var format = new CaptureFormat(_width, _height, Bt601: false, FullRange: false, $"{Describe()}, {_label}");
        lock (_deliver)
        {
            if (_paused) return;
            FormatChanged?.Invoke(format);
        }
    }

    private string Describe()
    {
        string scaled = _width != _sourceWidth || _height != _sourceHeight ? $" from {_sourceWidth}x{_sourceHeight}" : "";
        string turned = _rotation is ModeRotation.Rotate90 or ModeRotation.Rotate180 or ModeRotation.Rotate270
            ? $", turned upright from {_rotation}"
            : "";
        return $"NV12 {_width}x{_height}{scaled}, BT.709 limited range, from {_imageFormat}{turned}, " +
               "converted on the GPU and read back to memory";
    }

    // ----------------------------------------------------------------------------------
    // Opening.

    private enum OpenResult { Ok, Secure, Failed }

    /// <summary>Finds the display, makes the device and the duplication, and builds the conversion.</summary>
    private OpenResult Open(out string why)
    {
        // A factory goes stale when displays come and go; only a new one sees the change.
        if (_factory is null || !_factory.IsCurrent)
        {
            _factory?.Dispose();
            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        }

        if (!FindOutput(_factory, _source, out IDXGIAdapter1? adapter, out IDXGIOutput? output, out string label, out why))
            return OpenResult.Failed;

        using (adapter)
        using (output)
        {
            var luid = adapter!.Description1.Luid;
            long id = ((long)luid.HighPart << 32) | luid.LowPart;
            if (_device is null || _deviceLuid != id || _device.DeviceRemovedReason.Failure)
            {
                ReleaseDevice();
                try
                {
                    CreateDevice(adapter);
                    _deviceLuid = id;
                }
                catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
                {
                    why = ex is SharpGenException sg
                        ? $"{adapter.Description1.Description.Trim()} would not give a D3D11 device ({Hex(sg.ResultCode.Code)})"
                        : ex.Message;
                    ReleaseDevice();
                    return OpenResult.Failed;
                }
            }

            try
            {
                _duplication = Duplicate(output!);
            }
            catch (SharpGenException ex)
            {
                int code = ex.ResultCode.Code;
                why = code switch
                {
                    AccessDenied or DxgiAccessDenied => "the secure desktop is showing (the UAC prompt or the lock screen)",
                    NotCurrentlyAvailable => "too many programs are duplicating this display already",
                    SessionDisconnected => "this Windows session is not on a console (a disconnected remote desktop?)",
                    Unsupported => "this display cannot be duplicated in its current mode, or by this driver",
                    _ => $"desktop duplication would not start ({Hex(code)})",
                };
                if (code is DeviceRemoved or DeviceHung or DeviceReset) ReleaseDevice();
                return code is AccessDenied or DxgiAccessDenied ? OpenResult.Secure : OpenResult.Failed;
            }

            var mode = _duplication.Description;
            var refresh = mode.ModeDescription.RefreshRate;
            _refreshNs = refresh.Numerator > 0 && refresh.Denominator > 0
                ? (long)(1e9 * refresh.Denominator / refresh.Numerator)
                : 1_000_000_000 / 60;
            _label = label;

            try
            {
                var format = mode.ModeDescription.Format == Format.Unknown ? Format.B8G8R8A8_UNorm : mode.ModeDescription.Format;
                Build((int)mode.ModeDescription.Width, (int)mode.ModeDescription.Height, format, mode.Rotation);
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
            {
                why = ex is SharpGenException sg
                    ? $"the video processor would not convert this display's picture ({Hex(sg.ResultCode.Code)})"
                    : ex.Message;
                _duplication.Dispose();
                _duplication = null;
                return OpenResult.Failed;
            }
        }

        // A new duplication reports the pointer afresh, shape and all.
        _pointerShape = null;
        _pointerVisible = false;
        _pointer.Publish(this, new PointerFeed.State(false, 0, 0, _sourceWidth, _sourceHeight, null));
        why = "";
        return OpenResult.Ok;
    }

    /// <summary>
    /// The display <paramref name="source"/> names: its index among every display attached to
    /// the desktop, counted through the adapters in the order DXGI lists them, so that "0" is
    /// the first output of the first adapter that has one. A device name such as
    /// \\.\DISPLAY2 is taken too.
    /// </summary>
    private static bool FindOutput(IDXGIFactory1 factory, string source, out IDXGIAdapter1? found, out IDXGIOutput? output,
                                   out string label, out string why)
    {
        found = null;
        output = null;
        label = why = "";
        bool byIndex = int.TryParse(source, out int wanted);
        int count = 0;

        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            bool keep = false;
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? candidate).Success; o++)
            {
                var description = candidate.Description;
                bool attached = description.AttachedToDesktop;
                bool match = attached && (byIndex ? count == wanted : string.Equals(description.DeviceName, source,
                                                                                    StringComparison.OrdinalIgnoreCase));
                if (attached) count++;
                if (!match || keep)
                {
                    candidate.Dispose();
                    continue;
                }

                var rect = description.DesktopCoordinates;
                label = $"display {count - 1} ({description.DeviceName} on {adapter.Description1.Description.Trim()}, " +
                        $"{rect.Right - rect.Left}x{rect.Bottom - rect.Top} at {rect.Left},{rect.Top})";
                output = candidate;
                keep = true;
            }
            if (keep)
            {
                found = adapter;
                return true;
            }
            adapter.Dispose();
        }

        why = count == 0
            ? "no display is attached to the desktop"
            : byIndex
                ? $"there is no display {wanted}: displays are counted from 0, and there {(count == 1 ? "is 1" : $"are {count}")}"
                : $"no display is called {source}";
        return false;
    }

    private void CreateDevice(IDXGIAdapter1 adapter)
    {
        // The duplication has to be made with a device on the adapter that drives the
        // display; on a laptop that can be the integrated GPU rather than the one that renders.
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        _video = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterfaceOrNull<ID3D11VideoContext>();
        if (_video is null || _videoContext is null)
            throw new InvalidOperationException($"the driver of {adapter.Description1.Description.Trim()} has no D3D11 video processor");

        // The conversion is a moment's work for the GPU, but behind a game's frame it waits
        // for the whole of that frame. A higher priority lets it go first where the driver
        // allows; where it does not, nothing is lost.
        using var dxgi = _device.QueryInterfaceOrNull<IDXGIDevice>();
        if (dxgi is not null && dxgi.SetGPUThreadPriority(7).Failure)
            LinkLog.Debug("capture: the driver keeps the conversion at normal GPU priority");
    }

    private IDXGIOutputDuplication Duplicate(IDXGIOutput output)
    {
        // DuplicateOutput1 is told which formats this side takes: with BGRA alone, a desktop
        // in HDR is handed over already brought down to SDR, which is what an 8-bit NV12
        // stream can carry. DuplicateOutput is the older way, for older systems.
        using (var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>())
        {
            if (output5 is not null)
            {
                try
                {
                    return output5.DuplicateOutput1(_device!, [Format.B8G8R8A8_UNorm]);
                }
                catch (SharpGenException ex) when (ex.ResultCode.Code is not (AccessDenied or DxgiAccessDenied or
                                                                             NotCurrentlyAvailable or SessionDisconnected))
                {
                    LinkLog.Debug($"capture: DuplicateOutput1 refused ({Hex(ex.ResultCode.Code)}), trying DuplicateOutput");
                }
            }
        }
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        return output1.DuplicateOutput(_device!);
    }

    /// <summary>
    /// The conversion for a desktop image of this size, format and orientation: the size it
    /// goes out at, the video processor and its views, and the staging textures.
    /// </summary>
    private void Build(int imageWidth, int imageHeight, Format format, ModeRotation rotation)
    {
        ReleaseConversion();

        // The image comes the way the display scans it out; a display turned on its side has
        // a desktop that is upright only once the image is turned back.
        bool sideways = rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
        _imageWidth = imageWidth;
        _imageHeight = imageHeight;
        _imageFormat = format;
        _rotation = rotation;
        _sourceWidth = sideways ? imageHeight : imageWidth;
        _sourceHeight = sideways ? imageWidth : imageHeight;
        (_width, _height) = Fit(_sourceWidth, _sourceHeight, _requestWidth, _requestHeight);

        var device = _device!;
        _desktop = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)imageWidth,
            Height = (uint)imageHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)imageWidth,
            InputHeight = (uint)imageHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)_width,
            OutputHeight = (uint)_height,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _video!.CreateVideoProcessorEnumerator(content);
        if ((_enumerator.CheckVideoProcessorFormat(format) & VideoProcessorFormatSupport.Input) == 0)
            throw new InvalidOperationException($"the video processor does not take {format} pictures");
        if ((_enumerator.CheckVideoProcessorFormat(Format.NV12) & VideoProcessorFormatSupport.Output) == 0)
            throw new InvalidOperationException("the video processor does not make NV12 pictures");
        _processor = _video.CreateVideoProcessor(_enumerator, 0);

        _nv12 = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        for (int i = 0; i < StagingCount; i++)
        {
            _staging[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        _latest = -1;

        _inputView = _video.CreateVideoProcessorInputView(_desktop, _enumerator,
            new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        _outputView = _video.CreateVideoProcessorOutputView(_nv12, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        _streams = [new VideoProcessorStream { Enable = true, InputSurface = _inputView }];

        var vc = _videoContext!;
        vc.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        vc.VideoProcessorSetStreamSourceRect(_processor, 0, true, new RawRect(0, 0, imageWidth, imageHeight));
        vc.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, _width, _height));
        vc.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, _width, _height));

        // The driver's own touches - noise reduction, edge enhancement and the like - are for
        // films. A desktop is to arrive as it is.
        vc.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);

        // The direction DXGI gives is the one the image has to be turned to stand upright,
        // which the processor's rotation, clockwise, does.
        if (rotation is ModeRotation.Rotate90 or ModeRotation.Rotate180 or ModeRotation.Rotate270)
        {
            vc.VideoProcessorSetStreamRotation(_processor, 0, true, rotation switch
            {
                ModeRotation.Rotate90 => D3D11Rotation.Rotation90,
                ModeRotation.Rotate180 => D3D11Rotation.Rotation180,
                _ => D3D11Rotation.Rotation270,
            });
        }

        // Without colour spaces stated the driver guesses, and NV12 out of RGB is where they
        // guess BT.601. sRGB in, BT.709 studio range out - what the encoders then say in the
        // stream, and what the viewer decodes by.
        bool linear = format is Format.R16G16B16A16_Float;
        vc.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 2,
        });
        vc.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 1,
        });
        using (var vc1 = vc.QueryInterfaceOrNull<ID3D11VideoContext1>())
        {
            if (vc1 is not null)
            {
                vc1.VideoProcessorSetStreamColorSpace1(_processor, 0,
                    linear ? ColorSpaceType.RgbFullG10NoneP709 : ColorSpaceType.RgbFullG22NoneP709);
                vc1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
            }
        }
    }

    /// <summary>
    /// The size a picture of <paramref name="width"/> x <paramref name="height"/> goes out at
    /// when <paramref name="wantedWidth"/> x <paramref name="wantedHeight"/> is asked for: as
    /// large as fits with its shape kept, never larger than it is, and even on both sides, as
    /// 4:2:0 needs. Nothing asked for is its own size.
    /// </summary>
    private static (int Width, int Height) Fit(int width, int height, int wantedWidth, int wantedHeight)
    {
        if (wantedWidth <= 0 || wantedHeight <= 0) return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
        double scale = Math.Min(1.0, Math.Min((double)wantedWidth / width, (double)wantedHeight / height));
        int w = (int)(width * scale + 1e-6) & ~1, h = (int)(height * scale + 1e-6) & ~1;
        return (Math.Max(16, w), Math.Max(16, h));
    }

    // ----------------------------------------------------------------------------------
    // Letting go.

    private void ReleaseConversion()
    {
        _streams = [];
        _inputView?.Dispose();
        _inputView = null;
        _outputView?.Dispose();
        _outputView = null;
        for (int i = 0; i < StagingCount; i++)
        {
            _staging[i]?.Dispose();
            _staging[i] = null;
        }
        _latest = -1;
        _nv12?.Dispose();
        _nv12 = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _desktop?.Dispose();
        _desktop = null;
        _imageWidth = _imageHeight = 0;
    }

    private void ReleaseDevice()
    {
        _duplication?.Dispose();
        _duplication = null;
        ReleaseConversion();
        _videoContext?.Dispose();
        _videoContext = null;
        _video?.Dispose();
        _video = null;
        _context?.ClearState();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _deviceLuid = 0;
    }

    private void ReleaseAll()
    {
        ReleaseDevice();
        _factory?.Dispose();
        _factory = null;
    }

    private static string Hex(int code) => $"0x{code:X8}";
}
