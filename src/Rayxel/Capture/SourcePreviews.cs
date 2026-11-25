using System.Net;
using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Rayxel.Render;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>What a source's tile can say about its picture.</summary>
public enum PreviewState
{
    /// <summary>Being opened or asked; no picture yet.</summary>
    Starting,

    /// <summary>Pictures coming in.</summary>
    Live,

    /// <summary>Open, and nothing has come for a while: the source is asleep or has no signal.</summary>
    NoSignal,

    /// <summary>Another program has the card, or another computer has the Deck.</summary>
    Busy,

    /// <summary>The Deck is there with sending switched off.</summary>
    Off,

    /// <summary>The Deck did not answer.</summary>
    NotFound,

    /// <summary>The Deck no longer knows this computer: only pairing again helps.</summary>
    Forgotten,

    /// <summary>Anything else that stopped it.</summary>
    Failed
}

/// <summary>
/// Live pictures of the sources, for the page that lists them: every source but the one
/// being watched read on its own, a card in a small mode and a paired Deck at a low bitrate,
/// for as long as the page is open. The one being watched needs none - its tile is
/// the picture coming in (<see cref="Renderer.ActivePreview"/>).
/// <para>
/// A card feeds one program at a time, and a Deck shows its picture to one session at a time:
/// whatever is opened next must find them let go of. So everything here is stopped before a
/// source is opened (<see cref="StopAll"/>), and as soon as the page is left.
/// </para>
/// </summary>
public sealed class SourcePreviews : IDisposable
{
    // How long a source that could not be shown waits before it is tried again.
    private const int RetryMs = 4000;

    private readonly Renderer _renderer;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly Dictionary<string, PreviewFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);

    public SourcePreviews(Renderer renderer, IMFDXGIDeviceManager manager)
    {
        _renderer = renderer;
        _manager = manager;
    }

    /// <summary>
    /// Called every frame from the thread that owns the window. While <paramref name="wanted"/>,
    /// every source in <paramref name="devices"/> but <paramref name="watching"/> is read for its
    /// tile; otherwise nothing is.
    /// </summary>
    public void Update(IReadOnlyList<CaptureDeviceInfo> devices, CaptureDeviceInfo? watching, bool wanted)
    {
        _renderer.ShowActivePreview = wanted && watching is not null;
        if (!wanted)
        {
            StopAll();
            return;
        }

        long now = Environment.TickCount64;
        foreach (var (key, feed) in _feeds.ToList())
        {
            bool listed = devices.Any(d => string.Equals(d.SymbolicLink, key, StringComparison.OrdinalIgnoreCase));
            bool watched = watching is not null && string.Equals(watching.SymbolicLink, key, StringComparison.OrdinalIgnoreCase);
            if (listed && !watched && !(feed.Ended && feed.Retry && now >= feed.EndedMs + RetryMs)) continue;
            Stop(feed);
            _feeds.Remove(key);
        }

        foreach (var device in devices)
        {
            if (watching is not null && device.Is(watching)) continue;
            if (_feeds.ContainsKey(device.SymbolicLink) || NetDevice.Forgot(device)) continue;
            PreviewFeed feed = device.Bus == DeviceBus.Network
                ? new NetPreview(device, _renderer, _manager)
                : new CardPreview(device, _renderer, _manager);
            _renderer.AddPreview(feed.Surface);
            _feeds[device.SymbolicLink] = feed;
            feed.Start();
        }
    }

    /// <summary>The tile's feed for a source, or null for the one watched and for any not being read.</summary>
    public PreviewFeed? For(CaptureDeviceInfo device) =>
        _feeds.TryGetValue(device.SymbolicLink, out var feed) ? feed : null;

    /// <summary>
    /// Stops every feed and waits until each has let its source go, so that the source can be
    /// opened next. Called before any source is opened.
    /// </summary>
    public void StopAll()
    {
        if (_feeds.Count == 0) return;
        foreach (var feed in _feeds.Values) feed.BeginStop();
        foreach (var feed in _feeds.Values) Stop(feed);
        _feeds.Clear();
    }

    private void Stop(PreviewFeed feed)
    {
        feed.Dispose();
        _renderer.RemovePreview(feed.Surface);
    }

    public void Dispose() => StopAll();
}

/// <summary>One source's tile: its picture, and what can be said about it.</summary>
public abstract class PreviewFeed : IDisposable
{
    protected readonly CaptureDeviceInfo Device;
    protected readonly Renderer Renderer;
    protected readonly IMFDXGIDeviceManager Manager;
    protected volatile bool Running;
    private volatile int _state = (int)PreviewState.Starting;

    // A picture older than this means the source has gone quiet.
    private const int QuietMs = 1500;

    protected PreviewFeed(CaptureDeviceInfo device, Renderer renderer, IMFDXGIDeviceManager manager)
    {
        Device = device;
        Renderer = renderer;
        Manager = manager;
    }

    public PreviewSurface Surface { get; } = new();

    /// <summary>
    /// The mode the source is opened in when it is chosen - not the small one its tile is read
    /// in - for the tile to state. Null until known.
    /// </summary>
    public CaptureMode? Opens { get; protected set; }

    /// <summary>What the tile shows: the state, with a picture that has stopped coming read as no signal.</summary>
    public PreviewState State
    {
        get
        {
            var state = (PreviewState)_state;
            if (state != PreviewState.Live) return state;
            return Environment.TickCount64 - Surface.LastFrameMs > QuietMs && !RecentlyAlive ? PreviewState.NoSignal : state;
        }
    }

    // A sender that is there with nothing new to show says so; its picture is then still current.
    protected virtual bool RecentlyAlive => false;

    /// <summary>True once the feed has stopped by itself.</summary>
    public bool Ended { get; private set; }
    internal long EndedMs { get; private set; }

    /// <summary>Whether trying again a little later can change anything: not for a card with no mode to show.</summary>
    internal bool Retry { get; private set; } = true;

    protected void Say(PreviewState state) => _state = (int)state;

    protected void End(PreviewState state, string why, bool retry = true)
    {
        if (!Running) return;
        Say(state);
        Retry = retry;
        EndedMs = Environment.TickCount64;
        Ended = true;
        Log.Debug($"preview of '{Device.FriendlyName}': {state} - {why}");
    }

    protected void Picture(ID3D11Texture2D texture, int subresource, int width, int height, bool tenBit, bool hdr)
    {
        if (!Running) return;
        Renderer.UploadPreview(Surface, texture, subresource, width, height, tenBit, hdr);
        Say(PreviewState.Live);
    }

    public abstract void Start();

    /// <summary>Asks the feed to stop without waiting for it, so that several stop at once.</summary>
    public virtual void BeginStop() => Running = false;

    public abstract void Dispose();
}

/// <summary>
/// A capture card's tile: the card opened on a thread of its own, in its smallest 8-bit mode of
/// at least 640x360 at 30 fps or so. A card that is in another program's hands says so.
/// </summary>
internal sealed class CardPreview : PreviewFeed
{
    private Thread? _thread;
    private volatile IMFMediaSource? _source;

    public CardPreview(CaptureDeviceInfo device, Renderer renderer, IMFDXGIDeviceManager manager)
        : base(device, renderer, manager) { }

    public override void Start()
    {
        Running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "preview " + Device.FriendlyName };
        _thread.Start();
    }

    /// <summary>
    /// The mode to read a card's preview in: small, plain, and coming often enough to look
    /// alive - and in the proportions of its largest mode, which are the picture's own. A card
    /// fits whatever comes in into the mode asked for, so 720x480 squeezes a 16:9 picture.
    /// </summary>
    internal static CaptureMode? Pick(List<CaptureMode> modes)
    {
        var renderable = modes.Where(m => m.IsRenderable).ToList();
        if (renderable.Count == 0) return null;
        var largest = renderable.MaxBy(m => m.Width * m.Height)!;
        double aspect = largest.Width / (double)largest.Height;
        return renderable
            .OrderBy(m => m.Width * m.Height >= 640 * 360 && m.MaxFps >= 29.5 ? 0 : 1)
            .ThenBy(m => Math.Abs(m.Width / (double)m.Height - aspect) < 0.02 ? 0 : 1)
            .ThenBy(m => m.Format.IsHighBitDepth ? 1 : 0)
            .ThenBy(m => m.Transport == WireTransport.Raw ? 0 : 1)
            .ThenBy(m => m.Width * m.Height)
            .First();
    }

    private void Run()
    {
        IMFSourceReader? reader = null;
        try
        {
            _source = CaptureDevice.OpenSource(Device);
            if (!Running) return;
            reader = CaptureDevice.CreateReader(_source, Manager);
            var modes = CaptureDevice.EnumerateModes(reader, applyLinkBudget: !Device.IsVirtual);
            Opens = DeviceProbe.OpeningOf(modes);
            if (Pick(modes) is not { } mode)
            {
                End(PreviewState.Failed, "no mode with a picture this app can draw", retry: false);
                return;
            }

            using (var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, mode.TypeIndex))
            {
                double fps = Math.Clamp(30, mode.MinFps, mode.MaxFps);
                native.Set(MfGuids.FrameRate, MfGuids.Pack((uint)Math.Round(fps * 1000), 1000));
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, native);
            }

            int width, height;
            bool tenBit, hdr;
            using (var actual = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            {
                var format = VideoFormat.FromSubtype(actual.GetGUID(MfGuids.Subtype));
                var (w, h) = MfGuids.Unpack(actual.GetUInt64(MfGuids.FrameSize));
                (width, height) = ((int)w, (int)h);
                tenBit = format.IsHighBitDepth;
                hdr = tenBit && MfGuids.SaysHdr10(actual);
                Log.Debug($"preview of '{Device.FriendlyName}': {width}x{height} {format.Name}");
            }

            while (Running)
            {
                IMFSample? sample = null;
                try
                {
                    sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                                               out _, out var flags, out _);
                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        End(PreviewState.Failed, "the card ended the stream");
                        return;
                    }
                    if (sample is null || sample.BufferCount == 0) continue;

                    using var buffer = sample.GetBufferByIndex(0);
                    using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
                    if (dxgi is null) continue;
                    var pointer = dxgi.GetResource(MfGuids.ID3D11Texture2D);
                    if (pointer == IntPtr.Zero) continue;
                    using var texture = new ID3D11Texture2D(pointer);
                    Picture(texture, (int)dxgi.SubresourceIndex, width, height, tenBit, hdr);
                }
                finally { sample?.Dispose(); }
            }
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == unchecked((int)0xC00D3704))
        {
            // MF_E_HW_MFT_FAILED_START_STREAMING: what a card already in use looks like.
            End(PreviewState.Busy, "another program has the card");
        }
        catch (Exception ex)
        {
            End(PreviewState.Failed, ex.Message);
        }
        finally
        {
            reader?.Dispose();
            var source = _source;
            _source = null;
            try { source?.Shutdown(); } catch (SharpGenException) { }
            source?.Dispose();
        }
    }

    public override void Dispose()
    {
        BeginStop();
        if (_thread is { } thread && !thread.Join(400))
        {
            // A card with nothing coming in can keep a read waiting. Shutting the source down
            // lets the card go even then; it is not done first because a read under way when
            // the source is shut can wait on, where one left alone ends with the next frame.
            try { _source?.Shutdown(); } catch (SharpGenException) { } catch (ObjectDisposedException) { }
            if (!thread.Join(1600)) Log.Warn($"preview of '{Device.FriendlyName}' did not stop within 2 s");
        }
        _thread = null;
    }
}

/// <summary>
/// A Deck's tile: a session of its own, in HEVC at a low bitrate and rate, with nothing sent
/// back - no controller, no keys. A Deck shows its picture to one session at a time, and a
/// new one from this computer takes it over, so this is stopped before the Deck is opened
/// for watching.
/// <para>
/// It asks for the size watching does (<see cref="NetDevice.WatchMode"/>), not a small one:
/// the Deck's capture is made for one size, and opening the tile's Deck at another would
/// make it stop that capture and start a new one - a second's wait, and the one moment that
/// can bring gamescope down. Only the encoder changes, and that costs nothing to anyone.
/// </para>
/// </summary>
internal sealed class NetPreview : PreviewFeed, INetSink
{
    // 4 Mbit/s at 30 fps is a sharp enough picture for a tile, at any size.
    private const int Fps = 30, Kbps = 4000;

    private Task? _starting;
    private NetFeed? _feed;
    private long _aliveMs;

    public NetPreview(CaptureDeviceInfo device, Renderer renderer, IMFDXGIDeviceManager manager)
        : base(device, renderer, manager) => Opens = NetDevice.WatchMode(device);

    protected override bool RecentlyAlive => Environment.TickCount64 - Interlocked.Read(ref _aliveMs) < 1500;

    public override void Start()
    {
        Running = true;
        // Finding the Deck can take a second and more; not on the window's thread.
        _starting = Task.Run(() =>
        {
            if (!NetDevice.TryGetEndPoint(Device, out IPEndPoint endPoint, out string problem))
            {
                End(NetDevice.PeerOf(Device) is null ? PreviewState.Forgotten : PreviewState.NotFound, problem);
                return;
            }
            if (!Running) return;
            var watch = NetDevice.WatchMode(Device);
            var feed = new NetFeed(endPoint, NetDevice.PeerOf(Device), watch.Width, watch.Height, Fps, Manager, Renderer.Device,
                                   this, () => false, NetWire.Codec.Hevc, Kbps);
            lock (this)
            {
                if (!Running) return;
                _feed = feed;
                feed.Start();
            }
        });
    }

    bool INetSink.NetPicture(ID3D11Texture2D texture, int subresource, int width, int height, long captureTime)
    {
        Picture(texture, subresource, width, height, tenBit: false, hdr: false);
        return false;
    }

    void INetSink.NetLost(long frames) { }

    void INetSink.NetAlive()
    {
        Interlocked.Exchange(ref _aliveMs, Environment.TickCount64);
        if (State is PreviewState.Busy or PreviewState.Off) Say(PreviewState.Live);
    }

    void INetSink.NetFailed(string detail) => End(PreviewState.Failed, detail);

    void INetSink.NetRefused(Link.Wire.RefuseReason reason) => Say(reason switch
    {
        Link.Wire.RefuseReason.Busy => PreviewState.Busy,
        Link.Wire.RefuseReason.Disabled => PreviewState.Off,
        _ => PreviewState.Forgotten
    });

    public override void Dispose()
    {
        lock (this) Running = false;
        try { _starting?.Wait(3000); } catch (AggregateException) { }
        // Says goodbye to the Deck and waits for the thread, so the session is over before
        // another is begun.
        _feed?.Dispose();
        _feed = null;
    }
}
