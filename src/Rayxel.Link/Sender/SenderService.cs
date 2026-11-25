using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Rayxel.Link.Control;
using Rayxel.Link.Input;
using Rayxel.Link.Media;
using Rayxel.Link.Security;

namespace Rayxel.Link.Sender;

/// <summary>
/// The sender: waits for a paired viewer, captures and encodes for it, and plays its
/// controller, keyboard and mouse - the same on a Steam Deck and on Windows, with the
/// machine's own parts behind <see cref="ISenderPlatform"/>. It is what src/rayxel-send's
/// server.c was, plus who may connect: pairing, sealed sessions, being found, and being
/// switched on and off by a user interface.
/// <para>
/// Two threads do the work. The network thread reads the socket and ticks ten times a second
/// (timeouts, ALIVE, letting a capture go). The capture's own thread delivers frames, and
/// each is encoded and sent right there, as it arrives. Both take <see cref="_gate"/>. A
/// capture is never started, paused or disposed while holding it: those wait for the
/// capture's thread, which may itself be waiting for the gate in a frame callback.
/// </para>
/// <para>
/// One viewer at a time. The capture is this program's, not the viewer's: opened for the
/// first viewer and kept while viewers come and go, because each stop is a moment that can
/// bring gamescope down (see <see cref="ICaptureSource"/>). With nobody watching for
/// <see cref="IdleCaptureNs"/> it is let go all the same - paused, settled, closed, the safe
/// way - since a sender left switched on would otherwise spend the battery on a picture
/// nobody sees. Switching the sender off stops the stream, and the capture follows once the
/// same time has passed.
/// </para>
/// </summary>
public sealed class SenderService : IDisposable
{
    public const string Version = "2.0.0";

    // The viewer says HELLO twice a second; this long without one and it is gone.
    private const long ViewerTimeoutNs = 3_000_000_000;
    // How recently a viewer must have been heard from for another to be turned away. Shorter
    // than the timeout: a viewer that restarted without saying goodbye should get back in.
    private const long ViewerBusyNs = 1_500_000_000;
    private const long ReportEveryNs = 5_000_000_000;
    // With no frame to send for this long, say so instead.
    private const long AliveAfterNs = 150_000_000;
    // The viewer repeats its controller's state many times a second. This long without it
    // and the link is taken to be broken, and whatever was held is let go.
    private const long InputTimeoutNs = 300_000_000;
    // A CONNECT answered but never followed by a sealed HELLO is forgotten after this.
    private const long PendingTimeoutNs = 5_000_000_000;

    private readonly ISenderPlatform _platform;
    private readonly string _configDirectory;
    private readonly Identity _identity;
    private readonly PeerStore _viewers;
    private readonly object _gate = new();
    private SenderSettings _settings;
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;

    // Switches.
    private bool _enabled;
    private long _discoverableUntilMs;
    private SenderPairing? _pairing;
    private string? _problem;

    // The one viewer, when there is one.
    private sealed class Viewer
    {
        public required SocketAddress Address;
        public required string AddressText;
        public required uint Session;
        public SecureChannel? Channel;   // null: an unpaired viewer speaking version 1
        public Peer? Peer;
        public Wire.HelloMessage Wanted;
        public long LastHelloNs, LastSentNs;
        public byte Version => Channel is null ? Wire.Version1 : Wire.Version2;
    }

    private Viewer? _viewer;
    private long _lastRefusalNs;

    // CONNECTs answered with WELCOME, waiting for the first sealed HELLO.
    private sealed class PendingSession
    {
        public required Peer Peer;
        public required SecureChannel Channel;
        public required byte[] ViewerNonce;
        public required byte[] Welcome;
        public required long SinceNs;
    }

    private readonly Dictionary<(string Address, uint Session), PendingSession> _pending = [];
    private readonly Dictionary<string, byte[]> _linkSecrets = [];

    // The capture.
    private ICaptureSource? _capture;
    private int _heldWidth, _heldHeight;      // the size it was asked for; 0 is the source's own
    private bool _haveFormat, _captureBroken;
    private CaptureFormat _format;
    private ICaptureSource? _leaving;          // paused, waiting out SettleMs before it is disposed
    private long _leavingSinceNs;
    private long _idleSinceNs;                 // when the last viewer left a capture that is still held; 0 while one watches

    // How long a capture is kept with nobody watching. Held, it costs the source a second
    // picture of every frame and this program a copy of each - on a Deck's battery, for
    // nothing, if the sender was left on and the viewer is gone for good. Long enough that a
    // viewer that drops out and comes back, or goes from a tile to the whole screen, finds it
    // still there; after it, the next viewer waits the second a new one takes to start.
    private const long IdleCaptureNs = 30_000_000_000;
    private (SocketAddress Address, string Text, uint Session, Wire.HelloMessage Hello, SecureChannel? Channel, Peer? Peer)? _waiting;
    private byte[]? _kept;                     // the newest picture, for a viewer that comes while the screen is still
    private int _keptStride;
    private bool _keptValid;
    private ICursorSource? _cursor;

    // The encoder.
    private IVideoEncoder? _encoder;
    private Wire.Codec _codec;
    private readonly bool[] _refused = new bool[Wire.CodecCount];
    private readonly string?[] _refusedWhy = new string?[Wire.CodecCount];
    private bool _fallbackSaid, _formatChanged, _keyframeWanted, _broken;
    private string _brokenReason = "";
    private uint _frame;

    // The viewer's controller, keyboard and mouse, made when their first state arrives.
    private IPadSink? _pad;
    private IDeskSink? _desk;
    private bool _padHeard, _keysHeard, _mouseHeard, _inputRefused, _keySeen, _motionSeen;
    private uint _padSequence, _keysSequence, _mouseSequence;
    private int _mouseX, _mouseY;
    private long _lastPadNs, _lastDeskNs;

    // What was sent, for the log every few seconds and for the status every second.
    private struct Tally
    {
        public long SinceNs;
        public int Frames, Keyframes, Skipped;
        public long Bytes, EncodeNs, EncodeMaxNs, SendNs, SendMaxNs;
        public int Largest;
        public string? Unsent;
    }

    private Tally _report, _second;
    private StreamInfo? _lastSecond;

    // Buffers: one set for the capture's thread (video), one for the network thread.
    private readonly byte[] _videoPlain = new byte[Wire.VideoHeaderSize + Wire.PayloadMax];
    private readonly byte[] _videoSealed = new byte[Wire.DatagramMax];
    private readonly byte[] _netPlain = new byte[Wire.DatagramMax];
    private readonly byte[] _netSealed = new byte[Wire.DatagramMax];

    /// <summary>Something the user interfaces show changed. On whichever thread changed it; never under the gate.</summary>
    public event Action? StatusChanged;

    public SenderService(ISenderPlatform platform, string? configDirectory = null)
    {
        _platform = platform;
        _configDirectory = configDirectory ?? ControlPaths.ConfigDirectory();
        Directory.CreateDirectory(_configDirectory);
        _identity = Identity.LoadOrCreate(Path.Combine(_configDirectory, "identity.key"));
        _viewers = new PeerStore(Path.Combine(_configDirectory, "viewers.json"));
        _viewers.Changed += () => StatusChanged?.Invoke();
        _settings = LoadSettings();
        _enabled = _settings.StartEnabled;
    }

    public string Name => _settings.Name.Length > 0 ? _settings.Name : _platform.DefaultDeviceName;

    public Identity Identity => _identity;

    // ----------------------------------------------------------------------------------
    // Starting and stopping

    /// <summary>Opens the socket and starts the network thread. False, with the reason in the status, when the port cannot be had.</summary>
    public bool Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Room for a few frames, so a large one is never refused for want of buffer.
            socket.SendBufferSize = 4 << 20;
            socket.ReceiveBufferSize = 1 << 20;
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, _settings.Port));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            _problem = $"UDP port {_settings.Port} cannot be had: {ex.Message}";
            LinkLog.Error(_problem);
            return false;
        }
        IgnoreUnreachable(socket);
        _socket = socket;

        LinkLog.Info($"rayxel-send {Version} on {_platform.Name}: '{Name}', id {_identity.IdText}");
        LinkLog.Info($"listening: UDP port {_settings.Port}, source '{Source}', send buffer {socket.SendBufferSize / 1024} kB");
        LinkLog.Info("           nothing is captured until the first viewer asks; the capture is kept while viewers");
        LinkLog.Info($"           come and go, and let go after {IdleCaptureNs / 1_000_000_000} s with nobody watching");
        for (int i = 0; i < Wire.CodecCount; i++)
        {
            var codec = (Wire.Codec)i;
            bool can = _platform.CanEncode(codec, out string why);
            LinkLog.Info($"           {Wire.CodecName(codec),-8} : {(can ? "can be sent" : "not here - " + why)}");
            if (!can)
            {
                _refused[i] = true;
                _refusedWhy[i] = why;
            }
        }
        LinkLog.Info($"           {_viewers.All.Count} paired viewer(s); input from them {(_settings.AllowInput ? "accepted" : "not accepted")}" +
                     (_settings.AllowUnpaired ? "; unpaired viewers taken too, without input" : ""));
        LinkLog.Info(_enabled ? "           switched on" : "           switched off until a user interface switches it on");

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "network", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    private string Source => _settings.Source.Length > 0 ? _settings.Source : _platform.DefaultSource;

    /// <summary>
    /// Ends everything: the stream, then the capture - paused, given time to settle, closed.
    /// The one moment that can take gamescope down, which is why it is left to the end.
    /// </summary>
    public void Dispose()
    {
        if (!_running && _socket is null) return;
        _running = false;
        _thread?.Join(2000);

        ICaptureSource? capture, leaving;
        lock (_gate)
        {
            StopStream("shutting down");
            capture = _capture;
            leaving = _leaving;
            _capture = _leaving = null;
        }
        capture?.Pause();
        if (capture is not null || leaving is not null) Thread.Sleep(ICaptureSource.SettleMs);
        leaving?.Dispose();
        capture?.Dispose();
        _cursor?.Dispose();
        _socket?.Dispose();
        _socket = null;
        foreach (var pending in _pending.Values) pending.Channel.Dispose();
        _pending.Clear();
    }

    // ----------------------------------------------------------------------------------
    // What the user interfaces do

    public SenderSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public void UpdateSettings(SenderSettings settings)
    {
        lock (_gate)
        {
            settings.Name = Wire.Truncate(settings.Name);
            settings.DiscoverableMinutes = Math.Clamp(settings.DiscoverableMinutes, 1, 60);
            settings.BufferFrames = Math.Clamp(settings.BufferFrames, 0.5, 600);
            settings.MaxFrame = Math.Clamp(settings.MaxFrame, 0, 1000);
            settings.Gop = Math.Max(0, settings.Gop);
            if (settings.Port is <= 0 or > 65535) settings.Port = Wire.DefaultPort;
            bool inputOff = _settings.AllowInput && !settings.AllowInput;
            _settings = settings;
            SaveSettings();
            if (inputOff) CloseInput("input is no longer allowed");
        }
        StatusChanged?.Invoke();
    }

    /// <summary>One setting by itself, for a switch on a panel: nothing else is sent back and overwritten.</summary>
    public void SetAllowInput(bool allow)
    {
        var settings = Settings;
        settings.AllowInput = allow;
        UpdateSettings(settings);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            LinkLog.Info(enabled ? "switched on" : "switched off; the capture, if there is one, is kept");
            if (!enabled)
            {
                StopStream("the sender was switched off");
                _pairing = null;
                _discoverableUntilMs = 0;
            }
        }
        StatusChanged?.Invoke();
    }

    public void SetDiscoverable(bool discoverable)
    {
        lock (_gate)
        {
            if (discoverable && !_enabled)
            {
                _enabled = true;
                LinkLog.Info("switched on, to be found");
            }
            _discoverableUntilMs = discoverable ? Clock.UnixMs + _settings.DiscoverableMinutes * 60_000L : 0;
            if (!discoverable) _pairing = null;
            LinkLog.Info(discoverable ? $"may be found and paired with for {_settings.DiscoverableMinutes} min" : "no longer open to pairing");
        }
        StatusChanged?.Invoke();
    }

    public void CancelPairing()
    {
        lock (_gate)
        {
            if (_pairing is null) return;
            SendCancel(_pairing, Pairing.CancelReason.Cancelled);
            _pairing = null;
        }
        StatusChanged?.Invoke();
    }

    public bool Forget(string viewerId)
    {
        bool removed = _viewers.Remove(viewerId);
        lock (_gate)
        {
            _linkSecrets.Remove(viewerId);
            if (_viewer?.Peer?.Id == viewerId) StopStream("that viewer was forgotten");
        }
        if (removed) LinkLog.Info($"viewer {viewerId} forgotten");
        return removed;
    }

    public void StopStreamNow()
    {
        lock (_gate) StopStream("stopped from the user interface");
        StatusChanged?.Invoke();
    }

    /// <summary>A capture is open and kept: ending the program now is the risky moment.</summary>
    public bool CaptureHeld
    {
        get { lock (_gate) return _capture is not null || _leaving is not null; }
    }

    public SenderStatus Status()
    {
        lock (_gate)
        {
            bool discoverable = _enabled && _discoverableUntilMs > Clock.UnixMs;
            string state = !_enabled ? SenderStates.Off
                : _viewer is not null ? SenderStates.Streaming
                : _pairing is not null ? SenderStates.Pairing
                : discoverable ? SenderStates.Discoverable
                : SenderStates.Waiting;

            var status = new SenderStatus
            {
                Version = Version,
                Id = _identity.IdText,
                Name = Name,
                Platform = _platform.Name,
                Model = _platform.Model,
                Port = _settings.Port,
                Enabled = _enabled,
                State = state,
                DiscoverableUntilMs = discoverable ? _discoverableUntilMs : null,
                AllowInput = _settings.AllowInput,
                DefaultSource = _platform.DefaultSource,
                CaptureHeld = _capture is not null || _leaving is not null,
                Problem = _problem,
            };
            if (_pairing is { } pairing)
                status.Pairing = new PairingInfo { ViewerName = pairing.ViewerName, Pin = pairing.Pin, ExpiresMs = pairing.ExpiresMs };
            if (_viewer is { } viewer)
            {
                status.Viewer = new ViewerInfo
                {
                    Id = viewer.Peer?.Id ?? "",
                    Name = viewer.Peer?.Name ?? viewer.AddressText,
                    Address = viewer.AddressText,
                    Paired = viewer.Peer is not null,
                };
                status.Stream = _lastSecond ?? new StreamInfo { Codec = Wire.CodecName(viewer.Wanted.Codec) };
            }
            foreach (var peer in _viewers.All.OrderByDescending(p => p.LastSeenMs))
                status.Viewers.Add(new PairedViewer { Id = peer.Id, Name = peer.Name, PairedMs = peer.PairedMs, LastSeenMs = peer.LastSeenMs });
            for (int i = 0; i < Wire.CodecCount; i++)
                status.Codecs.Add(new CodecInfo { Name = Wire.CodecName((Wire.Codec)i), Available = !_refused[i], Why = _refusedWhy[i] });
            return status;
        }
    }

    // ----------------------------------------------------------------------------------
    // The network thread

    private void Run()
    {
        var socket = _socket!;
        var buffer = new byte[Wire.DatagramMax + 64];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        long nextTick = 0;
        while (_running)
        {
            try
            {
                if (socket.Poll(20_000, SelectMode.SelectRead))
                {
                    int n = socket.ReceiveFrom(buffer, SocketFlags.None, from);
                    if (n > 0) OnDatagram(buffer.AsSpan(0, n), from);
                }
            }
            catch (SocketException) { /* an ICMP error from a viewer that went away; nothing to do */ }
            catch (ObjectDisposedException) { break; }

            long now = Clock.NowNs;
            if (now >= nextTick)
            {
                Tick(now);
                nextTick = now + 100_000_000;
            }
        }
    }

    private void OnDatagram(ReadOnlySpan<byte> data, SocketAddress from)
    {
        byte type = Wire.TypeOf(data);
        if (type == 0) return;
        bool changed = false;

        lock (_gate)
        {
            if (Wire.VersionOf(data) == Wire.Version1)
            {
                if (_settings.AllowUnpaired && _enabled) OnPlain(type, data, from, ref changed);
            }
            else if (Wire.IsSealed(type))
            {
                OnSealed(type, data, from, ref changed);
            }
            else
            {
                switch (type)
                {
                    case Wire.Discover: OnDiscover(data, from); break;
                    case Wire.Connect: OnConnect(data, from); break;
                    case Wire.PairRequest: changed = OnPairRequest(data, from); break;
                    case Wire.PairReveal: changed = OnPairReveal(data, from); break;
                    case Wire.PairConfirm: changed = OnPairConfirm(data, from); break;
                    case Wire.PairCancel: changed = OnPairCancel(data, from); break;
                }
            }
        }
        if (changed) StatusChanged?.Invoke();
    }

    private static string Text(SocketAddress address)
    {
        var endPoint = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(address);
        return endPoint.ToString();
    }

    private bool FromViewer(SocketAddress from, uint session) =>
        _viewer is { } viewer && viewer.Session == session && viewer.Address.Equals(from);

    // --- discovery -------------------------------------------------------------------

    private void OnDiscover(ReadOnlySpan<byte> data, SocketAddress from)
    {
        if (!Wire.TryReadDiscover(data, out uint query, out var viewerId)) return;
        bool knows = _viewers.Find(viewerId) is not null;
        bool discoverable = _enabled && _discoverableUntilMs > Clock.UnixMs;
        // Strangers learn of this sender only while it may be found; a paired viewer always,
        // and switched off it says so, so the viewer can tell "off" from "gone".
        if (!discoverable && !knows) return;

        var flags = Wire.AnnounceFlags.None;
        if (discoverable) flags |= Wire.AnnounceFlags.Discoverable;
        if (knows) flags |= Wire.AnnounceFlags.KnowsYou;
        if (_viewer is not null) flags |= Wire.AnnounceFlags.Streaming;
        if (_enabled) flags |= Wire.AnnounceFlags.Enabled;
        byte codecs = 0;
        for (int i = 0; i < Wire.CodecCount; i++)
            if (!_refused[i]) codecs |= (byte)(1 << i);

        int length = Wire.WriteAnnounce(_netPlain, new Wire.AnnounceMessage(query, _identity.Id, flags, codecs, _settings.Port, Name, _platform.Model));
        Send(_netPlain.AsSpan(0, length), from);
    }

    // --- pairing ---------------------------------------------------------------------

    private bool OnPairRequest(ReadOnlySpan<byte> data, SocketAddress from)
    {
        uint number = Wire.NumberOf(data);
        if (!Pairing.TryReadRequest(data, out byte[] viewerId, out byte[] commitment, out string viewerName)) return false;

        if (!_enabled || _discoverableUntilMs <= Clock.UnixMs)
        {
            Send(_netPlain.AsSpan(0, Pairing.WriteCancel(_netPlain, number, Pairing.CancelReason.NotOpen)), from);
            return false;
        }
        if (_pairing is { } busy && !busy.Expired && busy.Number != number)
        {
            Send(_netPlain.AsSpan(0, Pairing.WriteCancel(_netPlain, number, Pairing.CancelReason.Busy)), from);
            return false;
        }

        bool fresh = _pairing is null || _pairing.Number != number || _pairing.Expired;
        if (fresh)
        {
            _pairing = new SenderPairing(_identity, number, viewerId, commitment, viewerName, Text(from));
            LinkLog.Info($"pairing: '{_pairing.ViewerName}' at {_pairing.FromAddress} asks to pair");
        }
        Send(_netPlain.AsSpan(0, _pairing!.WriteKey(_netPlain, Name, _platform.Model)), from);
        return fresh;
    }

    private bool OnPairReveal(ReadOnlySpan<byte> data, SocketAddress from)
    {
        if (_pairing is not { } pairing || pairing.Number != Wire.NumberOf(data) || pairing.FromAddress != Text(from)) return false;
        if (!Pairing.TryReadReveal(data, out byte[] key, out byte[] nonce)) return false;
        bool showing = pairing.Stage == SenderPairing.Stages.ShowingPin;
        if (pairing.OnReveal(key, nonce) is { } problem)
        {
            LinkLog.Warn($"pairing: {Pairing.Describe(problem)}");
            SendCancel(pairing, problem);
            _pairing = null;
            return true;
        }
        if (!showing) LinkLog.Info($"pairing: showing the PIN for '{pairing.ViewerName}'");
        return !showing;
    }

    private bool OnPairConfirm(ReadOnlySpan<byte> data, SocketAddress from)
    {
        if (_pairing is not { } pairing || pairing.Number != Wire.NumberOf(data) || pairing.FromAddress != Text(from)) return false;
        if (!Pairing.TryReadProof(data, out byte[] mac)) return false;
        if (pairing.Stage == SenderPairing.Stages.Done)
        {
            Send(_netPlain.AsSpan(0, pairing.WriteDone(_netPlain)), from);
            return false;
        }
        if (pairing.OnConfirm(mac) is not { } peer)
        {
            LinkLog.Warn("pairing: the viewer's proof did not check out");
            SendCancel(pairing, Pairing.CancelReason.BadProof);
            _pairing = null;
            return true;
        }

        _viewers.Put(peer);
        _linkSecrets.Remove(peer.Id);
        Send(_netPlain.AsSpan(0, pairing.WriteDone(_netPlain)), from);
        LinkLog.Info($"pairing: paired with '{peer.Name}' ({peer.Id})");
        // Done: no longer open to strangers. The pairing is kept a moment, so that a
        // CONFIRM resent because its DONE was lost is answered again; the tick drops it.
        _discoverableUntilMs = 0;
        return true;
    }

    private bool OnPairCancel(ReadOnlySpan<byte> data, SocketAddress from)
    {
        if (_pairing is not { } pairing || pairing.Number != Wire.NumberOf(data) || pairing.FromAddress != Text(from)) return false;
        LinkLog.Info($"pairing: '{pairing.ViewerName}' cancelled");
        _pairing = null;
        return true;
    }

    private void SendCancel(SenderPairing pairing, Pairing.CancelReason reason)
    {
        var target = new IPEndPoint(IPAddress.Any, 0);
        if (IPEndPoint.TryParse(pairing.FromAddress, out var endPoint)) target = endPoint;
        Send(_netPlain.AsSpan(0, Pairing.WriteCancel(_netPlain, pairing.Number, reason)), target.Serialize());
    }

    // --- sessions --------------------------------------------------------------------

    private void OnConnect(ReadOnlySpan<byte> data, SocketAddress from)
    {
        if (!Wire.TryReadConnect(data, out uint session, out var viewerId, out var viewerNonce)) return;
        string address = Text(from);

        if (!_enabled)
        {
            Refuse(session, Wire.RefuseReason.Disabled, from);
            return;
        }
        if (_viewers.Find(viewerId) is not { } peer)
        {
            LinkLog.Info($"viewer   : {address} is not paired with this sender; turned away");
            Refuse(session, Wire.RefuseReason.NotPaired, from);
            return;
        }
        long now = Clock.NowNs;
        if (_viewer is { } watching && watching.Peer?.Id != peer.Id && now - watching.LastHelloNs < ViewerBusyNs)
        {
            Refuse(session, Wire.RefuseReason.Busy, from);
            return;
        }

        // A CONNECT sent again because the WELCOME was lost is answered with the same WELCOME.
        if (_pending.TryGetValue((address, session), out var existing) && LinkKeys.Equal(existing.ViewerNonce, viewerNonce))
        {
            Send(existing.Welcome, from);
            return;
        }

        if (!_linkSecrets.TryGetValue(peer.Id, out byte[]? secret))
        {
            byte[] viewerKey = peer.PublicKeyBytes;
            secret = LinkKeys.LinkSecret(_identity, viewerKey, viewerKey, _identity.PublicKey);
            _linkSecrets[peer.Id] = secret;
        }
        byte[] senderNonce = LinkKeys.Random(Wire.NonceSize);
        byte[] nonce = viewerNonce.ToArray();
        byte[] proof = LinkKeys.Proof(secret, "welcome", nonce, senderNonce, LinkKeys.U32(session));
        var (toSender, toViewer) = LinkKeys.SessionKeys(secret, nonce, senderNonce, session);

        var welcome = new byte[Wire.IdSize + Wire.NonceSize + Wire.MacSize + 8];
        int length = Wire.WriteWelcome(welcome, session, _identity.Id, senderNonce, proof);
        if (_pending.Remove((address, session), out var replaced)) replaced.Channel.Dispose();
        _pending[(address, session)] = new PendingSession
        {
            Peer = peer,
            Channel = new SecureChannel(toViewer, toSender),
            ViewerNonce = nonce,
            Welcome = welcome[..length],
            SinceNs = now,
        };
        Send(welcome.AsSpan(0, length), from);
    }

    private void Refuse(uint session, Wire.RefuseReason reason, SocketAddress from) =>
        Send(_netPlain.AsSpan(0, Wire.WriteRefuse(_netPlain, session, reason)), from);

    private void OnSealed(byte type, ReadOnlySpan<byte> data, SocketAddress from, ref bool changed)
    {
        uint session = Wire.NumberOf(data);
        int length;

        // The viewer's own session first. A viewer that began its session again - it heard
        // nothing for a while - keeps its session number but has fresh keys, and waits among
        // the pending until its first HELLO opens there.
        if (FromViewer(from, session) && _viewer!.Channel is { } current && current.Open(data, _netPlain, out length))
        {
            OnMessage(type, _netPlain.AsSpan(0, length), from, ref changed);
            return;
        }
        if (!_pending.TryGetValue((Text(from), session), out var pending) || !pending.Channel.Open(data, _netPlain, out length))
            return;

        // The first sealed HELLO of a session makes its viewer the viewer.
        if (type != Wire.Hello || !Wire.TryReadHello(_netPlain.AsSpan(0, length), out var hello)) return;
        _pending.Remove((Text(from), session));
        if (!_enabled)
        {
            pending.Channel.Dispose();
            return;
        }
        if (_viewer is { } watching && watching.Peer?.Id != pending.Peer.Id && Clock.NowNs - watching.LastHelloNs < ViewerBusyNs)
        {
            pending.Channel.Dispose();
            Refuse(session, Wire.RefuseReason.Busy, from);
            return;
        }
        _viewers.Seen(pending.Peer.Id, Text(from));
        StartStream(CopyAddress(from), Text(from), hello, pending.Channel, pending.Peer);
        changed = true;
    }

    /// <summary>Version 1 from an unpaired viewer, taken only when the settings allow it, and never with input.</summary>
    private void OnPlain(byte type, ReadOnlySpan<byte> data, SocketAddress from, ref bool changed)
    {
        switch (type)
        {
            case Wire.Ping when Wire.TryReadPing(data, out long viewerTime):
                Send(_netPlain.AsSpan(0, Wire.WritePong(_netPlain, 0, viewerTime, Clock.NowNs, Wire.Version1)), from);
                return;
            case Wire.Hello when Wire.TryReadHello(data, out var hello):
                if (FromViewer(from, hello.Session))
                {
                    OnMessage(type, data, from, ref changed);
                }
                else if (_viewer is not null && Clock.NowNs - _viewer.LastHelloNs < ViewerBusyNs)
                {
                    NoteRefusal(Text(from));
                }
                else
                {
                    StartStream(CopyAddress(from), Text(from), hello, null, null);
                    changed = true;
                }
                return;
            case Wire.Keyframe or Wire.Bye:
                OnMessage(type, data, from, ref changed);
                return;
        }
    }

    private void NoteRefusal(string address)
    {
        if (Clock.NowNs - _lastRefusalNs <= ReportEveryNs) return;
        LinkLog.Info($"viewer   : {address} asked too, ignored while the first is watching");
        _lastRefusalNs = Clock.NowNs;
    }

    /// <summary>A message of the current viewer's session, opened if it was sealed.</summary>
    private void OnMessage(byte type, ReadOnlySpan<byte> plain, SocketAddress from, ref bool changed)
    {
        uint session = Wire.NumberOf(plain);
        if (!FromViewer(from, session)) return;
        var viewer = _viewer!;

        switch (type)
        {
            case Wire.Hello when Wire.TryReadHello(plain, out var hello):
                viewer.LastHelloNs = Clock.NowNs;
                OnCodec(hello.Codec);
                OnBitrate(hello.BitrateKbps);
                break;
            case Wire.Ping when Wire.TryReadPing(plain, out long viewerTime):
                SendToViewer(_netPlain.AsSpan(0, Wire.WritePong(_netPlain, session, viewerTime, Clock.NowNs, viewer.Version)), _netSealed);
                break;
            case Wire.Keyframe:
                _keyframeWanted = true;
                break;
            case Wire.Bye:
                StopStream("the viewer said goodbye");
                changed = true;
                break;
            case Wire.Pad when Wire.TryReadPad(plain, out _, out uint sequence, out var state):
                OnPad(sequence, state, ref changed);
                break;
            case Wire.Keys when Wire.TryReadKeys(plain, out _, out uint sequence, out var down):
                OnKeys(sequence, down, ref changed);
                break;
            case Wire.Mouse when Wire.TryReadMouse(plain, out var mouse):
                OnMouse(mouse, ref changed);
                break;
        }
    }

    private static SocketAddress CopyAddress(SocketAddress address)
    {
        var copy = new SocketAddress(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }

    // --- the stream ------------------------------------------------------------------

    private void StartStream(SocketAddress from, string address, Wire.HelloMessage hello, SecureChannel? channel, Peer? peer)
    {
        StopStream("replaced by a new request");

        // Too small to encode is taken as "no preference"; the source refuses nothing.
        int width = hello.Width >= 160 && hello.Height >= 128 ? hello.Width : 0;
        int height = width > 0 ? hello.Height : 0;

        // The capture kept from before serves any viewer that wants the same size. One that
        // is for another size, or that has stopped delivering - gamescope was restarted under
        // it - has to go first, and a request that comes meanwhile waits for it.
        if (_capture is not null && (width != _heldWidth || height != _heldHeight))
        {
            LinkLog.Info("capture  : let go, a different size is wanted");
            ReleaseCapture();
        }
        else if (_capture is not null && !_capture.Streaming)
        {
            LinkLog.Info("capture  : let go, it is no longer connected to the source");
            ReleaseCapture();
        }
        if (_leaving is not null)
        {
            _waiting?.Channel?.Dispose();
            _waiting = (from, address, hello.Session, hello, channel, peer);
            return;
        }

        _viewer = new Viewer
        {
            Address = from,
            AddressText = address,
            Session = hello.Session,
            Channel = channel,
            Peer = peer,
            Wanted = hello,
            LastHelloNs = Clock.NowNs,
            LastSentNs = Clock.NowNs,
        };
        _frame = 0;
        _padHeard = _keysHeard = _mouseHeard = _inputRefused = _keySeen = _motionSeen = false;
        _keyframeWanted = _fallbackSaid = false;
        // With a capture already running its format is known and will not be announced
        // again, so the encoder is opened for it by the first frame.
        _formatChanged = _capture is not null && _haveFormat;
        _report = _second = new Tally { SinceNs = Clock.NowNs };
        _lastSecond = null;

        string who = peer is not null ? $"'{peer.Name}' ({address})" : $"{address}, unpaired";
        LinkLog.Info($"viewer   : {who} asks for " + (width > 0 ? $"{width}x{height}" : "the source's own size") +
                     $", {Wire.CodecName(hello.Codec)} at {(hello.BitrateKbps > 0 ? hello.BitrateKbps : DefaultKbps(hello.Codec)) / 1000.0:0.#} Mbit/s, " +
                     $"{(hello.Fps > 0 ? hello.Fps : 60)} fps");

        _cursor ??= _platform.CreateCursor();
        if (_capture is not null)
        {
            // A new viewer of a capture that is already running changes nothing on screen, and
            // gamescope draws only when something does. The newest picture is still what is
            // showing, so the viewer gets that one to begin with.
            if (_keptValid && _haveFormat) SendKept();
            return;
        }

        var capture = _platform.CreateCapture(Source);
        capture.FormatChanged += format => OnFormat(capture, format);
        capture.Frame += (in CapturedFrame frame) => OnFrame(capture, frame);
        capture.Failed += reason => OnCaptureFailed(capture, reason);
        _capture = capture;
        _heldWidth = width;
        _heldHeight = height;
        _haveFormat = false;
        // Started off the gate: connecting waits for the capture's thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (capture.Start(width, height, out string why)) return;
            lock (_gate)
            {
                if (_capture != capture) return;
                _capture = null;
                StopStream($"the video source could not be opened: {why}");
            }
            capture.Dispose();
            StatusChanged?.Invoke();
        });
    }

    /// <summary>The viewer is gone or being replaced. The capture stays as it is, until the tick finds it idle.</summary>
    private void StopStream(string why)
    {
        if (_viewer is not { } viewer) return;
        LinkLog.Info($"stopped  : {why} ({viewer.Peer?.Name ?? viewer.AddressText})");
        _encoder?.Dispose();
        _encoder = null;
        CloseInput(null);
        viewer.Channel?.Dispose();
        _viewer = null;
        _broken = false;
        _lastSecond = null;
    }

    private void CloseInput(string? why)
    {
        if (why is not null && (_pad is not null || _desk is not null)) LinkLog.Info($"input    : {why}");
        _pad?.Dispose();
        _pad = null;
        _desk?.Dispose();
        _desk = null;
    }

    /// <summary>Gives the capture up: paused by a worker now, disposed by the tick once it has settled.</summary>
    private void ReleaseCapture()
    {
        if (_capture is not { } capture) return;
        _leaving = capture;
        _leavingSinceNs = Clock.NowNs;
        _capture = null;
        _haveFormat = _keptValid = _captureBroken = false;
        ThreadPool.QueueUserWorkItem(_ => capture.Pause());
    }

    private void OnFormat(ICaptureSource capture, CaptureFormat format)
    {
        lock (_gate)
        {
            if (capture != _capture) return;
            LinkLog.Info($"capture  : {format.Description}");
            _format = format;
            _haveFormat = true;
            _formatChanged = true;
        }
    }

    private void OnCaptureFailed(ICaptureSource capture, string reason)
    {
        lock (_gate)
        {
            if (capture != _capture) return;
            _captureBroken = true;
            _brokenReason = reason;
        }
    }

    private void OnFrame(ICaptureSource capture, in CapturedFrame frame)
    {
        lock (_gate)
        {
            if (capture != _capture || !_haveFormat) return;
            if (frame.Width != _format.Width || frame.Height != _format.Height || frame.Length < frame.Needed) return;

            // Kept whether or not anybody is watching: it is what the next viewer needs.
            int needed = frame.Needed;
            if (_kept is null || _kept.Length != needed)
            {
                _kept = GC.AllocateUninitializedArray<byte>(needed, pinned: true);
                _keptValid = false;
            }
            frame.Span[..needed].CopyTo(_kept);
            _keptStride = frame.Stride;
            _keptValid = true;

            if (_viewer is null || _broken) return;
            if (_formatChanged && !OpenEncoder()) return;
            if (_encoder is null) return;
            SendPicture(frame);
        }
    }

    private unsafe void SendKept()
    {
        if (!OpenEncoder()) return;
        fixed (byte* data = _kept)
        {
            var frame = new CapturedFrame(data, _kept!.Length, _keptStride, _format.Width, _format.Height, Clock.NowNs);
            SendPicture(frame);
        }
    }

    /// <summary>What each codec is asked to spend when the viewer names no figure.</summary>
    private static int DefaultKbps(Wire.Codec codec) => codec switch
    {
        Wire.Codec.Av1 => 15_000,
        Wire.Codec.PyroWave => 140_000,
        _ => 20_000,
    };

    /// <summary>
    /// A new size, bitrate or codec is a new encoder, and its first frame a keyframe. The
    /// codec asked for, or HEVC when this machine cannot do it: the viewer learns which from
    /// every datagram and decodes accordingly.
    /// </summary>
    private bool OpenEncoder()
    {
        var viewer = _viewer!;
        _encoder?.Dispose();
        _encoder = null;
        _formatChanged = false;

        var wanted = (int)viewer.Wanted.Codec < Wire.CodecCount ? viewer.Wanted.Codec : Wire.Codec.Hevc;
        int fps = viewer.Wanted.Fps > 0 ? viewer.Wanted.Fps : 60;
        if (wanted != Wire.Codec.Hevc && _refused[(int)wanted] && !_fallbackSaid)
        {
            LinkLog.Info($"codec    : {Wire.CodecName(wanted)} cannot be had here; sending HEVC instead");
            _fallbackSaid = true;
        }

        foreach (var codec in (Wire.Codec[])[wanted, Wire.Codec.Hevc])
        {
            if (_encoder is not null || (codec != Wire.Codec.Hevc && _refused[(int)codec])) continue;
            // A figure meant for one codec is no use to another: PyroWave's would swamp HEVC,
            // HEVC's would starve PyroWave. The viewer's goes to the codec it asked for.
            int kbps = codec == wanted && viewer.Wanted.BitrateKbps > 0 ? viewer.Wanted.BitrateKbps : DefaultKbps(codec);
            var config = new EncoderConfig(codec, _format.Width, _format.Height, fps, kbps, _settings.BufferFrames,
                                           _settings.MaxFrame > 0 ? (int)(kbps * 125.0 / fps * _settings.MaxFrame) : 0,
                                           _settings.Gop, _format.Bt601, _format.FullRange);
            _encoder = _platform.OpenEncoder(config, out string why);
            if (_encoder is not null)
            {
                _codec = codec;
            }
            else if (codec != Wire.Codec.Hevc)
            {
                _refused[(int)codec] = true;
                _refusedWhy[(int)codec] = why;
                _fallbackSaid = true;
                LinkLog.Info($"codec    : {Wire.CodecName(codec)} cannot be had here ({why}); sending HEVC instead");
            }
            else
            {
                LinkLog.Warn($"codec    : HEVC cannot be had either: {why}");
            }
        }

        if (_encoder is null)
        {
            _broken = true;
            _brokenReason = "the encoder could not be opened";
            return false;
        }
        LinkLog.Info($"stream   : {_encoder.Description}");
        return true;
    }

    /// <summary>One picture to the viewer: encoded, cut into datagrams, sealed and sent.</summary>
    private void SendPicture(in CapturedFrame frame)
    {
        var viewer = _viewer!;
        CursorOverlay? pointer = _cursor?.Current(_format.Width, _format.Height, _format.Bt601, _format.FullRange);

        bool keyframe = _keyframeWanted;
        _keyframeWanted = false;
        if (!_encoder!.Encode(frame, keyframe, pointer, out var encoded))
        {
            _broken = true;
            _brokenReason = $"encoding failed: {_encoder.Error}";
            return;
        }
        long encodedNs = Clock.NowNs;

        string? unsent = SendFrame(viewer, encoded, frame.ArrivalNs, encodedNs);
        long done = Clock.NowNs;
        viewer.LastSentNs = done;

        if (unsent is null)
        {
            Count(ref _report, encoded, frame.ArrivalNs, encodedNs, done);
            Count(ref _second, encoded, frame.ArrivalNs, encodedNs, done);
        }
        else
        {
            // The viewer never got this frame, so the next one cannot refer to it.
            _report.Skipped++;
            _report.Unsent = unsent;
            _keyframeWanted = true;
        }
        _frame++;
    }

    private static void Count(ref Tally t, in EncodedFrame encoded, long arrivalNs, long encodedNs, long doneNs)
    {
        t.Frames++;
        t.Bytes += encoded.Length;
        if (encoded.Keyframe) t.Keyframes++;
        t.Largest = Math.Max(t.Largest, encoded.Length);
        t.EncodeNs += encodedNs - arrivalNs;
        t.EncodeMaxNs = Math.Max(t.EncodeMaxNs, encodedNs - arrivalNs);
        t.SendNs += doneNs - encodedNs;
        t.SendMaxNs = Math.Max(t.SendMaxNs, doneNs - encodedNs);
    }

    /// <summary>The frame as datagrams, each its header and the next slice. Null when sent, else why not.</summary>
    private string? SendFrame(Viewer viewer, in EncodedFrame encoded, long captureNs, long encodedNs)
    {
        int count = (encoded.Length + Wire.PayloadMax - 1) / Wire.PayloadMax;
        if (count == 0) return "an empty frame";
        if (count > Wire.MaxPackets) return "more packets than a frame may have";

        var socket = _socket!;
        var data = encoded.Span;
        for (int i = 0; i < count; i++)
        {
            int offset = i * Wire.PayloadMax;
            int length = Math.Min(Wire.PayloadMax, encoded.Length - offset);
            Wire.WriteVideoHeader(_videoPlain, new Wire.VideoHeader(
                viewer.Session, _frame, i, count, encoded.Length, _format.Width, _format.Height, encoded.Keyframe, _codec,
                captureNs, (int)((encodedNs - captureNs) / 1000)), viewer.Version);
            data.Slice(offset, length).CopyTo(_videoPlain.AsSpan(Wire.VideoHeaderSize));
            var plain = _videoPlain.AsSpan(0, Wire.VideoHeaderSize + length);
            try
            {
                if (viewer.Channel is { } channel)
                    socket.SendTo(_videoSealed.AsSpan(0, channel.Seal(plain, _videoSealed)), SocketFlags.None, viewer.Address);
                else
                    socket.SendTo(plain, SocketFlags.None, viewer.Address);
            }
            catch (SocketException ex)
            {
                // Whatever went wrong, the viewer is missing part of a frame and will ask for
                // a keyframe; there is nothing better to do with the rest of it.
                return ex.SocketErrorCode.ToString();
            }
        }
        return null;
    }

    private void OnBitrate(int kbps)
    {
        var viewer = _viewer!;
        if (kbps <= 0 || kbps == viewer.Wanted.BitrateKbps) return;
        int before = viewer.Wanted.BitrateKbps > 0 ? viewer.Wanted.BitrateKbps : DefaultKbps(viewer.Wanted.Codec);
        LinkLog.Info($"bitrate  : {before / 1000.0:0.#} -> {kbps / 1000.0:0.#} Mbit/s, as the viewer asks");
        viewer.Wanted = viewer.Wanted with { BitrateKbps = kbps };
        if (_haveFormat) _formatChanged = true;
    }

    private void OnCodec(Wire.Codec codec)
    {
        var viewer = _viewer!;
        if (codec == viewer.Wanted.Codec) return;
        LinkLog.Info($"codec    : {Wire.CodecName(viewer.Wanted.Codec)} -> {Wire.CodecName(codec)}, as the viewer asks");
        viewer.Wanted = viewer.Wanted with { Codec = codec };
        _fallbackSaid = false;
        if (_haveFormat) _formatChanged = true;
    }

    // --- input -----------------------------------------------------------------------

    /// <summary>Input is taken only from a paired viewer the picture is going to, and only when the settings allow it.</summary>
    private bool InputAllowed(string what)
    {
        if (_settings.AllowInput && _viewer?.Peer is not null && !_inputRefused) return true;
        if (!_inputRefused)
            LinkLog.Info(_viewer?.Peer is null
                ? $"input    : the viewer is sending its {what}, ignored - it is not paired"
                : $"input    : the viewer is sending its {what}, ignored - input is not allowed in the settings");
        _inputRefused = true;
        return false;
    }

    private void OnPad(uint sequence, in PadState state, ref bool changed)
    {
        // Datagrams can overtake each other; a state older than the one in force is stale.
        if (_padHeard && (int)(sequence - _padSequence) <= 0) return;
        if (!InputAllowed("controller")) return;
        _padHeard = true;
        _padSequence = sequence;
        _lastPadNs = Clock.NowNs;

        if (_pad is null)
        {
            _pad = _platform.OpenPad(out string why);
            if (_pad is null)
            {
                LinkLog.Info($"gamepad  : cannot be made here: {why}");
                _inputRefused = true; // said why once; do not retry on every datagram
                return;
            }
            LinkLog.Info("gamepad  : the viewer's controller is connected here");
            changed = true;
        }
        _pad.Apply(state);
    }

    private bool DeskReady()
    {
        if (!InputAllowed("keyboard and mouse")) return false;
        if (_desk is null)
        {
            _desk = _platform.OpenDesk(out string why);
            if (_desk is null)
            {
                LinkLog.Info($"keyboard : cannot be made here: {why}");
                _inputRefused = true;
                return false;
            }
            LinkLog.Info("keyboard : the viewer's keyboard and mouse are connected here");
        }
        _lastDeskNs = Clock.NowNs;
        return true;
    }

    private void OnKeys(uint sequence, ReadOnlySpan<byte> down, ref bool changed)
    {
        if (_keysHeard && (int)(sequence - _keysSequence) <= 0) return;
        if (!DeskReady()) return;
        _keysHeard = true;
        _keysSequence = sequence;
        _desk!.Keys(down);
        // Not which key - that is nobody's business but the game's - only that one came.
        if (!_keySeen && _desk.Active)
        {
            _keySeen = true;
            LinkLog.Info("keyboard : the first key press has arrived");
        }
    }

    private void OnMouse(in Wire.MouseMessage mouse, ref bool changed)
    {
        if (_mouseHeard && (int)(mouse.Sequence - _mouseSequence) <= 0) return;
        if (!DeskReady()) return;
        if (_mouseHeard && !_motionSeen && (mouse.X != _mouseX || mouse.Y != _mouseY))
        {
            _motionSeen = true;
            LinkLog.Info("keyboard : the first mouse movement has arrived");
        }
        _mouseX = mouse.X;
        _mouseY = mouse.Y;
        _mouseHeard = true;
        _mouseSequence = mouse.Sequence;
        _desk!.Mouse(mouse.Buttons, mouse.X, mouse.Y, mouse.Wheel, mouse.HWheel);
    }

    // --- the tick --------------------------------------------------------------------

    private void Tick(long now)
    {
        bool changed = false;
        ICaptureSource? dispose = null;

        lock (_gate)
        {
            if (_captureBroken)
            {
                // With or without a viewer: a capture that has failed is no use to the next one.
                if (_viewer is not null) StopStream(_brokenReason);
                else LinkLog.Info($"capture  : lost while nobody was watching ({_brokenReason})");
                ReleaseCapture();
                _broken = false;
                changed = true;
            }
            else if (_broken)
            {
                StopStream(_brokenReason);
                changed = true;
            }
            else if (_viewer is { } quiet && now - quiet.LastHelloNs > ViewerTimeoutNs)
            {
                StopStream("the viewer went quiet");
                changed = true;
            }

            // Nobody watching for a while: the capture goes, the safe way, as for a new size.
            if (_viewer is null && _capture is not null && _waiting is null)
            {
                if (_idleSinceNs == 0) _idleSinceNs = now;
                else if (now - _idleSinceNs >= IdleCaptureNs)
                {
                    LinkLog.Info($"capture  : let go, nobody has watched for {IdleCaptureNs / 1_000_000_000} s");
                    ReleaseCapture();
                    _idleSinceNs = 0;
                    changed = true;
                }
            }
            else _idleSinceNs = 0;

            if (_leaving is not null && now - _leavingSinceNs >= ICaptureSource.SettleMs * 1_000_000L)
            {
                dispose = _leaving;
                _leaving = null;
                changed = true;
            }

            // A keyframe asked for while nothing on screen changes would wait for the next
            // change: the source sends no frame for a still picture. The picture kept from
            // the last one is still what is showing, so the keyframe is made from that.
            if (_viewer is { } waiting && _keyframeWanted && _keptValid && _haveFormat && _encoder is not null &&
                now - waiting.LastSentNs >= AliveAfterNs)
                SendKept();

            if (_viewer is { } viewer && now - viewer.LastSentNs >= AliveAfterNs)
            {
                SendToViewer(_netPlain.AsSpan(0, Wire.WriteSession(_netPlain, Wire.Alive, viewer.Session, viewer.Version)), _netSealed);
                viewer.LastSentNs = now;
            }

            // Frames are what make the pointer get looked at; without them, this does.
            _cursor?.Poll();

            // A held button must not outlive the link it was held over.
            if (_pad is { Active: true } && now - _lastPadNs > InputTimeoutNs)
            {
                _pad.Release();
                LinkLog.Info($"gamepad  : nothing heard for {(now - _lastPadNs) / 1e9:0.0} s, everything released");
            }
            if (_desk is { Active: true } && now - _lastDeskNs > InputTimeoutNs)
            {
                _desk.Release();
                LinkLog.Info($"keyboard : nothing heard for {(now - _lastDeskNs) / 1e9:0.0} s, every key and button released");
            }

            if (_viewer is not null && now - _second.SinceNs >= 1_000_000_000)
            {
                _lastSecond = Summarize(_second, now);
                _second = new Tally { SinceNs = now };
                changed = true;
            }
            if (_viewer is not null && now - _report.SinceNs >= ReportEveryNs) Report(now);

            foreach (var stale in _pending.Where(p => now - p.Value.SinceNs > PendingTimeoutNs).Select(p => p.Key).ToList())
            {
                _pending[stale].Channel.Dispose();
                _pending.Remove(stale);
            }

            if (_pairing is { } pairing && (pairing.Expired ||
                (pairing.Stage == SenderPairing.Stages.Done && Clock.UnixMs > pairing.ExpiresMs - Pairing.TimeoutMs + 10_000)))
            {
                if (pairing.Stage != SenderPairing.Stages.Done) LinkLog.Info("pairing: timed out");
                _pairing = null;
                changed = true;
            }
            if (_discoverableUntilMs != 0 && _discoverableUntilMs <= Clock.UnixMs)
            {
                _discoverableUntilMs = 0;
                LinkLog.Info("no longer open to pairing: time is up");
                changed = true;
            }
        }

        if (dispose is not null)
        {
            dispose.Dispose();
            lock (_gate)
            {
                if (_waiting is { } waiting && _leaving is null)
                {
                    _waiting = null;
                    StartStream(waiting.Address, waiting.Text, waiting.Hello, waiting.Channel, waiting.Peer);
                }
            }
        }
        if (changed) StatusChanged?.Invoke();
    }

    private StreamInfo Summarize(in Tally t, long now)
    {
        double seconds = Math.Max(1e-3, (now - t.SinceNs) / 1e9);
        return new StreamInfo
        {
            Codec = Wire.CodecName(_codec),
            AskedCodec = _viewer is { } v && v.Wanted.Codec != _codec ? Wire.CodecName(v.Wanted.Codec) : null,
            Width = _format.Width,
            Height = _format.Height,
            Fps = t.Frames / seconds,
            Mbps = t.Bytes * 8 / seconds / 1e6,
            BitrateKbps = _viewer is { } w && w.Wanted.BitrateKbps > 0 ? w.Wanted.BitrateKbps : DefaultKbps(_codec),
            EncodeMs = t.Frames > 0 ? t.EncodeNs / (double)t.Frames / 1e6 : 0,
            EncodeMaxMs = t.EncodeMaxNs / 1e6,
            Encoder = _encoder?.Description ?? "",
            InputActive = _pad is not null || _desk is not null,
        };
    }

    private void Report(long now)
    {
        var t = _report;
        double seconds = (now - t.SinceNs) / 1e9;
        if (t.Frames > 0 && seconds > 0)
            LinkLog.Info($"  {t.Frames / seconds,5:0.0} fps  {t.Bytes * 8 / seconds / 1e6,6:0.00} Mbit/s {Wire.CodecName(_codec)}  " +
                         $"encode {t.EncodeNs / (double)t.Frames / 1e6:0.00} ms (max {t.EncodeMaxNs / 1e6:0.00})  " +
                         $"send {t.SendNs / (double)t.Frames / 1e6:0.00} ms (max {t.SendMaxNs / 1e6:0.00})  " +
                         $"largest {t.Largest / 1024.0:0} kB  {t.Keyframes} keyframe{(t.Keyframes == 1 ? "" : "s")}" +
                         (t.Skipped > 0 ? $", {t.Skipped} frame(s) not sent: {t.Unsent}" : ""));
        else if (t.Skipped > 0)
            LinkLog.Info($"  {t.Skipped} frame(s) not sent: {t.Unsent}");
        else
            LinkLog.Info("  no frames - the source draws only when something on screen changes");
        _report = new Tally { SinceNs = now };
    }

    // --- sending ---------------------------------------------------------------------

    private void Send(ReadOnlySpan<byte> datagram, SocketAddress to)
    {
        try { _socket?.SendTo(datagram, SocketFlags.None, to); }
        catch (SocketException) { }
    }

    /// <summary>A message of the viewer's session, sealed when the session is.</summary>
    private void SendToViewer(ReadOnlySpan<byte> plain, byte[] sealedBuffer)
    {
        if (_viewer is not { } viewer) return;
        if (viewer.Channel is { } channel) Send(sealedBuffer.AsSpan(0, channel.Seal(plain, sealedBuffer)), viewer.Address);
        else Send(plain, viewer.Address);
    }

    /// <summary>
    /// On Windows a datagram sent to a port nobody listens on comes back as an error on the
    /// next receive, and would end the wait for everyone else's. Told not to, it does not.
    /// </summary>
    private static void IgnoreUnreachable(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try { socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); }
        catch (SocketException) { }
    }

    // --- settings --------------------------------------------------------------------

    private string SettingsPath => Path.Combine(_configDirectory, "settings.json");

    private SenderSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), ControlJson.Default.SenderSettings) ?? new SenderSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LinkLog.Warn($"settings: {SettingsPath} could not be read ({ex.Message}); using defaults");
        }
        return new SenderSettings();
    }

    private void SaveSettings()
    {
        try
        {
            var options = new JsonSerializerOptions(ControlJson.Default.Options) { WriteIndented = true };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new ControlJson(options).SenderSettings));
        }
        catch (IOException ex)
        {
            LinkLog.Warn($"settings: {SettingsPath} could not be written: {ex.Message}");
        }
    }

    /// <summary>Overrides for this run only, from the command line: not saved.</summary>
    public void Override(Action<SenderSettings> change)
    {
        lock (_gate) change(_settings);
    }
}
