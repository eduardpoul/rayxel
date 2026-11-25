using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rayxel.Diagnostics;
using Rayxel.Link.Security;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>Where a frame's time went between the Deck and this process, for the run so far.</summary>
/// <param name="EncodeMs">On the Deck: frame in hand to encoded frame in hand.</param>
/// <param name="NetworkMs">Encoded frame in hand on the Deck to its last packet here.</param>
/// <param name="DecodeMs">Last packet here to the picture decoded on the GPU and in our texture.</param>
/// <param name="ClockKnown">False until a ping has come back; without it the network figure is unknown.</param>
/// <param name="DecodeKnown">
/// False while the decode is not being timed. Timing it means waiting for the GPU, which is
/// only done while somebody is looking at the figure.
/// </param>
/// <param name="AskedKbps">What the Deck is being asked to spend now.</param>
/// <param name="Codec">What the frames arriving are, or "-" before the first.</param>
/// <param name="AskedCodec">What the Deck is being asked for. Not the same when it cannot encode it.</param>
/// <param name="CodecRefused">
/// The frames have gone on being of another codec than the one asked for, for longer than
/// a change-over takes: the Deck cannot encode it.
/// </param>
public sealed record NetStats(
    double EncodeMs, double NetworkMs, double DecodeMs, double RoundTripMs, bool ClockKnown, bool DecodeKnown,
    long FramesLost, long PacketsLate, long KeyframesRequested, double Mbps, string Decoder, int AskedKbps,
    string Codec, string AskedCodec, bool CodecRefused);

/// <summary>Every frame's figures over a measurement window, for percentiles.</summary>
public sealed record NetRecording(double[] EncodeMs, double[] NetworkMs, double[] DecodeMs, double[] FrameKb);

/// <summary>What a <see cref="NetFeed"/> tells the pump. Called on the feed's own thread.</summary>
internal interface INetSink
{
    /// <param name="captureTime">
    /// When the Deck got the frame from gamescope, on this machine's Media Foundation clock,
    /// or 0 while the two clocks have not been compared yet.
    /// </param>
    /// <returns>True when the call waited for the GPU to finish decoding the picture.</returns>
    bool NetPicture(ID3D11Texture2D texture, int subresource, int width, int height, long captureTime);

    /// <summary>Frames that will never be shown: a packet of theirs did not arrive.</summary>
    void NetLost(long frames);

    /// <summary>The sender is there and has nothing new to show.</summary>
    void NetAlive();

    void NetFailed(string detail);

    /// <summary>
    /// The sender turned this computer away: switched off, showing its picture to another
    /// computer, or no longer paired with this one. Said each time it happens; a picture or
    /// a sign of life afterwards ends it.
    /// </summary>
    void NetRefused(Link.Wire.RefuseReason reason);
}

/// <summary>
/// Receives a Steam Deck's picture from <c>rayxel-send</c> and decodes it: the network
/// counterpart of the loop that reads a capture card.
/// <para>
/// One thread does all of it — keeps the stream requested, puts frames back together from
/// their packets, decodes them, and measures the difference between the Deck's clock and
/// ours so that a frame's age can be stated in our own time. It is one thread on purpose:
/// a frame is only worth decoding once its last packet is in, and nothing is gained by
/// handing it to someone else at that moment.
/// </para>
/// </summary>
internal sealed class NetFeed : IDisposable, IPictureSink
{
    private readonly IPEndPoint _sender;
    private readonly int _width, _height, _fps;

    // A paired sender, whose sessions are begun with a handshake and sealed; null for one
    // named by address, the old kind, spoken to in the clear.
    private readonly Peer? _peer;
    private NetLink? _link;
    private long _lastHeardNs;

    // What the Deck is asked for: a codec, and what to spend - fixed, or followed by the
    // governor. The HELLO says it twice a second, and at once when it changes.
    private readonly BitrateGovernor _governor = new(NetDevice.AdaptiveCeilingKbps);
    private volatile int _askedKbps;
    private volatile NetWire.Codec _askedCodec;
    private long _askedCodecAtNs;
    private bool _pyroWaveRefusalSaid;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly ID3D11Device _device;
    private readonly INetSink _sink;
    private readonly Func<bool> _focused;
    private volatile DeskForwarder? _desk;
    private readonly uint _session = (uint)Random.Shared.Next(1, int.MaxValue);

    private Thread? _thread;
    private volatile bool _running;

    // HEVC and AV1 go through Windows' decoders, PyroWave through its own. Whichever the
    // frames say they are; only one is open at a time.
    private MfDecoder? _decoder;
    private PyroWaveDecoder? _pyroWave;
    private (int Width, int Height) _decoderSize;
    private NetWire.Codec _arriving;
    private bool _arrivedAny;
    private volatile string _arrivingName = "-";

    // The frame being put together.
    private byte[] _frame = new byte[512 * 1024];
    private readonly bool[] _have = new bool[NetWire.MaxPackets];
    private NetWire.VideoHeader _head;
    private bool _hasFrame;
    private int _got;
    private long _completedNs;
    private bool _waitingForKeyframe = true;
    private long _keyframeAskedNs;

    // Clock difference, sender minus viewer, taken from the quickest round trips seen: the
    // less time a ping spent in transit, the less room there is for the two legs to differ.
    private readonly (long RoundTrip, long Offset)[] _pings = new (long, long)[64];
    private int _pingCount;
    private long _offsetNs, _bestRoundTripNs;
    private bool _clockKnown;

    private readonly object _statsGate = new();
    private double _encodeMs, _networkMs, _decodeMs, _mbps;
    private bool _decodeKnown;
    private long _framesLost, _packetsLate, _keyframesRequested;
    private long _bytesInWindow, _windowStartNs;
    private double[]? _logEncode, _logNetwork, _logDecode, _logKb;
    private int _logCount;

    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;

    /// <summary>This machine's clock in nanoseconds. Media Foundation's is the same one in 100 ns units.</summary>
    private static long NowNs() => (long)(Stopwatch.GetTimestamp() * NsPerTick);

    // A codec and a bitrate of its own, for a stream that is not the one being watched: the
    // settings' choice is for that one.
    private readonly NetWire.Codec? _fixedCodec;
    private readonly int _fixedKbps;

    public NetFeed(IPEndPoint sender, Peer? peer, int width, int height, int fps,
                   IMFDXGIDeviceManager deviceManager, ID3D11Device device, INetSink sink, Func<bool> focused,
                   NetWire.Codec? codec = null, int kbps = 0)
    {
        _fixedCodec = codec;
        _fixedKbps = kbps;
        _focused = focused;
        _sender = sender;
        _peer = peer;
        _width = width;
        _height = height;
        _fps = fps;
        _askedCodec = WantedCodec();
        _askedKbps = Wanted(_askedCodec);
        _deviceManager = deviceManager;
        _device = device;
        _sink = sink;
    }

    /// <summary>
    /// The codec to ask for now: the one chosen, except PyroWave when its library is not
    /// here to decode it — then HEVC, as the Deck itself would answer a codec it lacks.
    /// </summary>
    private NetWire.Codec WantedCodec()
    {
        var codec = _fixedCodec ?? NetDevice.Codec;
        if (codec != NetWire.Codec.PyroWave || PyroWaveDecoder.Problem is not { } problem) return codec;
        if (!_pyroWaveRefusalSaid)
        {
            Log.Warn($"PyroWave cannot be decoded here, so HEVC is asked for instead: {problem}");
            _pyroWaveRefusalSaid = true;
        }
        return NetWire.Codec.Hevc;
    }

    /// <summary>
    /// The bitrate to ask for now: PyroWave's own figure, or for HEVC and AV1 the fixed one
    /// or whatever the governor has come to.
    /// </summary>
    private int Wanted(NetWire.Codec codec) =>
        _fixedKbps > 0 ? _fixedKbps
        : codec == NetWire.Codec.PyroWave ? NetDevice.PyroWaveKbps
        : NetDevice.Adaptive ? _governor.Current : NetDevice.BitrateKbps;

    // Whether the governor sets the bitrate: not for PyroWave, whose figure is fixed, nor for
    // a stream given one of its own.
    private bool Governed(NetWire.Codec codec) => _fixedKbps <= 0 && NetDevice.Adaptive && codec != NetWire.Codec.PyroWave;

    /// <summary>What the frames arriving are, or "-" before the first. Read by the pump to say so.</summary>
    public string ArrivingCodec => _arrivingName;

    /// <summary>True while the keyboard and mouse are being sent to the Deck instead of used here.</summary>
    public bool InputCaptured => _desk?.Captured == true;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "network", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Room for a couple of seconds. While one frame is being decoded nobody is reading,
            // and whatever arrives in that time has to wait here rather than be dropped - and
            // at the start the wait is longer: the first picture can switch the window to HDR,
            // which holds the renderer, and this thread with it, for a second or more. At
            // PyroWave's 140 Mbit/s eight megabytes lasted less than half a second of that,
            // and a hundred frames were lost at the start of every stream.
            socket.ReceiveBufferSize = 32 << 20;
            socket.Connect(_sender);
            var link = new NetLink(socket, secure: _peer is not null);
            _link = link;

            // A paired sender first proves who it is, and the session's keys are agreed;
            // until then nothing else is sent. One that does not answer may be starting up,
            // so it is asked again until this feed is stopped.
            if (_peer is not null && !Handshake(link)) return;

            // The controller goes the other way down the same connection, on a thread of
            // its own: it has to be looked at a thousand times a second, and this one
            // spends its time waiting for packets.
            using var pad = new PadForwarder(link, _session, _focused);
            pad.Start();

            // And the keyboard and mouse, once the user hands them over.
            using var desk = new DeskForwarder(link, _session, _focused);
            desk.Start();
            _desk = desk;

            // PyroWave's decoder is made before it is needed: its Vulkan device and shaders
            // take a good part of a second the first time, and frames that arrived meanwhile
            // would wait in the socket and come out too late to show.
            if (_askedCodec == NetWire.Codec.PyroWave) OpenPyroWave(_width, _height);

            Volatile.Write(ref _askedCodecAtNs, NowNs());
            byte[] packet = new byte[NetWire.DatagramMax + 64];
            byte[] opened = new byte[NetWire.DatagramMax + 64];
            byte[] outgoing = new byte[64];
            _lastHeardNs = NowNs();
            long nextHello = 0, nextPing = 0;
            int pingsSent = 0;
            _windowStartNs = NowNs();

            Log.Info($"asking {_sender} for {_width}x{_height} {NetDevice.CodecName(_askedCodec)}, " +
                     $"{_askedKbps / 1000.0:0.#} Mbit/s at {_fps} fps" +
                     (Governed(_askedCodec)
                         ? $", following the link up to {NetDevice.AdaptiveCeilingKbps / 1000.0:0.#}" : ""));

            while (_running)
            {
                long now = NowNs();

                // The codec and the bitrate, which the HELLO carries. A sender since these
                // were added takes a new one up while the stream runs; an older one keeps
                // the bitrate it first heard, and sends HEVC whatever is asked.
                var codec = WantedCodec();
                if (codec != _askedCodec)
                {
                    Log.Info($"codec: {NetDevice.CodecName(_askedCodec)} -> {NetDevice.CodecName(codec)}");
                    _askedCodec = codec;
                    Volatile.Write(ref _askedCodecAtNs, now);
                    nextHello = now;
                }
                // PyroWave's figure is fixed; the governor is for the codecs it was made for.
                bool governed = Governed(codec);
                if (governed) _governor.Judge(now, NetDevice.AdaptiveCeilingKbps);
                int wanted = Wanted(codec);
                if (wanted != _askedKbps)
                {
                    if (!governed) Log.Info($"bitrate: {wanted / 1000.0:0.#} Mbit/s, fixed");
                    _askedKbps = wanted;
                    nextHello = now;
                }

                // A paired sender that has gone silent - restarted, or switched off and on - has
                // forgotten this session; it is begun again with fresh keys.
                if (_peer is not null && now - _lastHeardNs > 3_000_000_000)
                {
                    Log.Info($"nothing from {_peer.Name} for 3 s; beginning the session again");
                    if (!Handshake(link)) break;
                    _lastHeardNs = NowNs();
                    nextHello = nextPing = 0;
                    _waitingForKeyframe = true;
                    continue;
                }

                if (now >= nextHello)
                {
                    link.Send(outgoing.AsSpan(0, NetWire.WriteHello(outgoing, _session, _width, _height, _fps, _askedCodec, _askedKbps,
                                                                    link.Version)));
                    nextHello = now + 500_000_000;
                }
                if (now >= nextPing)
                {
                    link.Send(outgoing.AsSpan(0, NetWire.WritePing(outgoing, _session, now, link.Version)));
                    // Often at first, so there is a good comparison of the clocks before the
                    // first frames are judged by it; then just enough to follow their drift.
                    nextPing = now + (++pingsSent < 20 ? 50_000_000 : 250_000_000);
                }

                if (!socket.Poll(5_000, SelectMode.SelectRead)) continue;
                int n = socket.Receive(packet, 0, packet.Length, SocketFlags.None, out SocketError error);
                long arrived = NowNs();
                // A sender that is not listening answers with an ICMP error, which arrives
                // here as a failed receive. It may simply not have started yet; keep asking.
                if (error != SocketError.Success || n <= 0) continue;

                var data = link.Open(packet.AsSpan(0, n), opened);
                if (data.IsEmpty) continue;
                if (NetWire.TypeOf(data) == NetWire.Refuse)
                {
                    // The sender has turned this session away while it ran: switched off, or
                    // given to another computer. Said once; the session is begun again later.
                    if (NetWire.TryReadRefuse(data, out uint refused, out var reason) && refused == _session)
                    {
                        Log.Info($"{_peer?.Name ?? "the sender"}: {ViewerHandshake.Describe(reason)}");
                        _sink.NetRefused(reason);
                    }
                    continue;
                }
                _lastHeardNs = arrived;
                switch (NetWire.TypeOf(data))
                {
                    case NetWire.Pong:
                        if (NetWire.TryReadPong(data, out long sent, out long senderTime))
                            NoteClock(arrived - sent, senderTime - (sent + (arrived - sent) / 2));
                        break;
                    case NetWire.Alive:
                        if (NetWire.TryReadSession(data, out uint session) && session == _session) _sink.NetAlive();
                        break;
                    case NetWire.Video:
                        if (NetWire.TryReadVideo(data, out var header) && header.Session == _session)
                            OnPacket(header, data[NetWire.VideoHeaderSize..], arrived);
                        break;
                }
            }

            pad.Dispose();
            _desk = null;
            desk.Dispose();
            link.Send(outgoing.AsSpan(0, NetWire.WriteSession(outgoing, NetWire.Bye, _session, link.Version)));
        }
        catch (Exception ex)
        {
            if (_running)
            {
                Log.Error("network source stopped", ex);
                _sink.NetFailed(ex.Message);
            }
        }
        finally
        {
            _decoder?.Dispose();
            _decoder = null;
            _pyroWave?.Dispose();
            _pyroWave = null;
            socket?.Dispose();
        }
    }

    /// <summary>
    /// Begins a session with the paired sender: proves both sides and agrees its keys, asking
    /// again every couple of seconds while the sender does not answer. False when this feed
    /// is stopped meanwhile, or the sender refuses in a way asking again will not change.
    /// </summary>
    private bool Handshake(NetLink link)
    {
        string? said = null;
        while (_running)
        {
            try
            {
                link.Channel = ViewerHandshake.Connect(link.Socket, NetLinks.Identity, _peer!, _session);
                NetLinks.Store.Seen(_peer!.Id, _sender.ToString());
                Log.Info($"session with {_peer.Name} begun: sealed, keys of its own");
                return true;
            }
            catch (LinkRefusedException ex) when (ex.Reason is Link.Wire.RefuseReason.NotPaired or Link.Wire.RefuseReason.BadProof)
            {
                // The sender has forgotten this computer, or holds another key: only pairing again helps.
                Log.Error($"{_peer!.Name}: {ex.Message}");
                _sink.NetRefused(ex.Reason!.Value);
                _sink.NetFailed(ex.Message);
                return false;
            }
            catch (LinkRefusedException ex)
            {
                if (said != ex.Message) Log.Info($"{_peer!.Name}: {ex.Message}; asking again");
                said = ex.Message;
                if (ex.Reason is { } reason) _sink.NetRefused(reason);
                for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
            }
        }
        return false;
    }

    private void NoteClock(long roundTripNs, long offsetNs)
    {
        if (roundTripNs < 0) return;
        _pings[_pingCount++ % _pings.Length] = (roundTripNs, offsetNs);

        int filled = Math.Min(_pingCount, _pings.Length);
        var best = _pings[0];
        for (int i = 1; i < filled; i++)
            if (_pings[i].RoundTrip < best.RoundTrip) best = _pings[i];

        _offsetNs = best.Offset;
        _bestRoundTripNs = best.RoundTrip;
        if (!_clockKnown)
        {
            _clockKnown = true;
            Log.Info($"clocks compared: round trip {best.RoundTrip / 1e6:0.00} ms, so frame ages are good to about half that");
        }
    }

    private void OnPacket(in NetWire.VideoHeader h, ReadOnlySpan<byte> payload, long arrived)
    {
        if ((int)h.Codec >= NetWire.CodecCount) return; // from a sender newer than this viewer
        bool pyroWave = h.Codec == NetWire.Codec.PyroWave;

        // Left over from a frame already given up on - unless the sender has started over.
        // It does when it is restarted, or when it stopped hearing from us for a while:
        // its count begins again from nothing, and everything it sends from then on looks
        // old. A keyframe with an old number, or a number far behind, is a new beginning
        // and not a straggler; taking it for one left the picture gone for good. Every
        // PyroWave frame is a keyframe, so for it only the distance tells.
        if (_hasFrame && (int)(h.Frame - _head.Frame) < 0)
        {
            bool startedOver = (h.Keyframe && !pyroWave) || (int)(_head.Frame - h.Frame) > 60;
            if (!startedOver)
            {
                Interlocked.Increment(ref _packetsLate);
                return;
            }
            _hasFrame = false;
            _waitingForKeyframe = true;
            // PyroWave counts frames of its own as well, and its encoder began that count
            // again too; a decoder that remembers the old one takes the new frames for stale.
            if (pyroWave) ClosePyroWave();
        }

        if (!_hasFrame || h.Frame != _head.Frame)
        {
            // A newer frame has started. The one before it is lost if it never became
            // whole, and so is every frame none of whose packets arrived; either way what
            // follows refers to pictures we do not have, until the next keyframe. PyroWave
            // has none to wait for: its next frame is whole by itself.
            if (_hasFrame)
            {
                long missing = (_got < _head.Count ? 1 : 0) + (long)(h.Frame - _head.Frame - 1);
                if (missing > 0)
                {
                    Interlocked.Add(ref _framesLost, missing);
                    if (_head.Codec != NetWire.Codec.PyroWave) _governor.Lost(arrived, missing);
                    _sink.NetLost(missing);
                    _waitingForKeyframe = true;
                }
            }

            if (h.Count <= 0 || h.Count > NetWire.MaxPackets || h.FrameBytes <= 0 ||
                h.FrameBytes > h.Count * NetWire.PayloadMax)
                return;

            if (!_arrivedAny || h.Codec != _arriving) NoteCodec(h.Codec);

            _head = h;
            _hasFrame = true;
            _got = 0;
            Array.Clear(_have, 0, h.Count);
            if (_frame.Length < h.FrameBytes) _frame = new byte[h.FrameBytes + h.FrameBytes / 2];
        }

        int offset = h.Index * NetWire.PayloadMax;
        if (h.Index >= _head.Count || h.Count != _head.Count || _have[h.Index] ||
            offset + payload.Length > _head.FrameBytes)
            return;

        payload.CopyTo(_frame.AsSpan(offset));
        _have[h.Index] = true;
        if (++_got < _head.Count) return;

        // The frame is whole: what the governor judges the link by, with how long it took.
        _completedNs = arrived;
        if (pyroWave)
        {
            DecodePyroWave(arrived);
            return;
        }
        _governor.Frame(arrived, _clockKnown
            ? (arrived - (_head.CaptureTimeNs - _offsetNs)) / 1e6 - _head.EncodeUs / 1000.0
            : double.NaN);
        if (_head.Keyframe) _waitingForKeyframe = false;
        if (_waitingForKeyframe)
        {
            AskForKeyframe(arrived);
            return;
        }
        DecodeFrame(arrived);
    }

    /// <summary>
    /// The frames have begun to be of another codec: the one asked for has arrived, or the
    /// Deck cannot do it and sends HEVC. A new codec is a new stream. The decoder of the
    /// old one goes, and HEVC and AV1 start again from a keyframe, as the Deck's new
    /// encoder does by itself.
    /// </summary>
    private void NoteCodec(NetWire.Codec codec)
    {
        string name = NetDevice.CodecName(codec);
        if (codec != _askedCodec)
            Log.Info($"the Deck sends {name}: it cannot encode {NetDevice.CodecName(_askedCodec)}, or has not changed over yet");
        else
            Log.Info(_arrivedAny ? $"the Deck now sends {name}" : $"the Deck sends {name}");

        _arriving = codec;
        _arrivedAny = true;
        _arrivingName = name;
        _decoder?.Dispose();
        _decoder = null;
        // One made ahead for the first frames is kept.
        if (codec != NetWire.Codec.PyroWave) ClosePyroWave();
        _waitingForKeyframe = true;
    }

    private void OpenPyroWave(int width, int height)
    {
        ClosePyroWave();
        try
        {
            _pyroWave = new PyroWaveDecoder(_device, width, height);
            _decoderSize = (width, height);
            Log.Info($"decoding {width}x{height} PyroWave with '{_pyroWave.Name}'");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or EntryPointNotFoundException)
        {
            // No library, or a GPU it will not run on: nothing later will fix that.
            Log.Error($"cannot decode: {ex.Message}");
            _running = false;
            _sink.NetFailed(ex.Message);
        }
    }

    private void ClosePyroWave()
    {
        _pyroWave?.Dispose();
        _pyroWave = null;
    }

    private void DecodePyroWave(long arrived)
    {
        if (_pyroWave is null || _decoderSize != (_head.Width, _head.Height)) OpenPyroWave(_head.Width, _head.Height);
        if (_pyroWave is null) return;

        // The whole frame at once: the codec reads its packets one after another.
        bool taken = _pyroWave.Push(_frame.AsSpan(0, _head.FrameBytes));

        // Not whole by the codec's count, though every byte came: it took the frame for an
        // older one. That happens when the Deck's encoder is opened again - for a new
        // bitrate or size - and its own count of frames starts over with it. A new decoder
        // knows no count, and takes the frame.
        if (!taken || !_pyroWave.Ready)
        {
            Log.Info($"PyroWave frame {_head.Frame} was not taken by the decoder; it starts again");
            OpenPyroWave(_head.Width, _head.Height);
            if (_pyroWave is null) return;
            if (!_pyroWave.Push(_frame.AsSpan(0, _head.FrameBytes)) || !_pyroWave.Ready)
            {
                Interlocked.Increment(ref _framesLost);
                _sink.NetLost(1);
                return;
            }
        }

        try
        {
            long captureTime = _clockKnown ? (_head.CaptureTimeNs - _offsetNs) / 100 : 0;
            _pyroWave.Decode(captureTime, this);
        }
        catch (IOException ex)
        {
            Log.Warn($"PyroWave decode failed, starting the decoder again: {ex.Message}");
            OpenPyroWave(_head.Width, _head.Height);
        }
    }

    /// <summary>At most a few times a second: the answer takes a frame or two to arrive, and asking again in the meantime changes nothing.</summary>
    private void AskForKeyframe(long now)
    {
        if (now - _keyframeAskedNs < 250_000_000) return;
        _keyframeAskedNs = now;
        Interlocked.Increment(ref _keyframesRequested);

        if (_link is not { } link) return;
        Span<byte> request = stackalloc byte[8];
        link.Send(request[..NetWire.WriteSession(request, NetWire.Keyframe, _session, link.Version)]);
    }

    private void DecodeFrame(long arrived)
    {
        try
        {
            if (_decoder is null || _decoder.Codec != _head.Codec || _decoderSize != (_head.Width, _head.Height))
            {
                _decoder?.Dispose();
                _decoder = null;
                _decoder = new MfDecoder(_deviceManager, _head.Codec, _head.Width, _head.Height);
                _decoderSize = (_head.Width, _head.Height);
                Log.Info($"decoding {_head.Width}x{_head.Height} {NetDevice.CodecName(_head.Codec)} with '{_decoder.Name}'");
            }

            // The capture time goes in as the sample's timestamp and comes back out on the
            // picture, so each picture is matched to the frame it came from even if the
            // decoder were to hold one back.
            long captureTime = _clockKnown ? (_head.CaptureTimeNs - _offsetNs) / 100 : 0;
            _decoder.Decode(_frame.AsSpan(0, _head.FrameBytes), captureTime, this);
        }
        catch (InvalidOperationException ex)
        {
            // No decoder, or one that will not use the GPU: nothing later will fix that.
            Log.Error($"cannot decode: {ex.Message}");
            _running = false;
            _sink.NetFailed(ex.Message);
        }
        catch (Exception ex)
        {
            // A frame the decoder choked on. Start again from a clean decoder and a keyframe.
            Log.Warn($"decode failed, asking for a keyframe: {ex.Message}");
            _decoder?.Dispose();
            _decoder = null;
            _waitingForKeyframe = true;
            AskForKeyframe(arrived);
        }
    }

    void IPictureSink.OnPicture(ID3D11Texture2D texture, int subresource, long sampleTime)
    {
        double encodeMs = _head.EncodeUs / 1000.0;
        double networkMs = _clockKnown
            ? (_completedNs - (_head.CaptureTimeNs - _offsetNs)) / 1e6 - encodeMs
            : double.NaN;

        // The decoder has only accepted the frame at this point; the decoding itself is the
        // GPU's and still ahead. When the pump waits for it, the time from the last packet
        // to here is the decode. When it does not, there is no honest figure to give.
        bool decoded = _sink.NetPicture(texture, subresource, _head.Width, _head.Height, sampleTime);
        long now = NowNs();
        double decodeMs = decoded ? (now - _completedNs) / 1e6 : double.NaN;

        lock (_statsGate)
        {
            static double Blend(double average, double sample) =>
                average == 0 ? sample : average * 0.9 + sample * 0.1;

            _encodeMs = Blend(_encodeMs, encodeMs);
            if (!double.IsNaN(networkMs)) _networkMs = Blend(_networkMs, networkMs);
            if (decoded) _decodeMs = Blend(_decodeMs, decodeMs);
            _decodeKnown = decoded;

            _bytesInWindow += _head.FrameBytes;
            if (now - _windowStartNs >= 1_000_000_000)
            {
                _mbps = _bytesInWindow * 8.0 / ((now - _windowStartNs) / 1e9) / 1e6;
                _bytesInWindow = 0;
                _windowStartNs = now;
            }

            if (_logEncode is not null && _logCount < _logEncode.Length)
            {
                _logEncode[_logCount] = encodeMs;
                _logNetwork![_logCount] = networkMs;
                _logDecode![_logCount] = decodeMs;
                _logKb![_logCount] = _head.FrameBytes / 1024.0;
                _logCount++;
            }
        }
    }

    public NetStats Snapshot()
    {
        lock (_statsGate)
        {
            return new NetStats(_encodeMs, _networkMs, _decodeMs, _bestRoundTripNs / 1e6, _clockKnown, _decodeKnown,
                Interlocked.Read(ref _framesLost), Interlocked.Read(ref _packetsLate),
                Interlocked.Read(ref _keyframesRequested), _mbps, _pyroWave?.Name ?? _decoder?.Name ?? "-", _askedKbps,
                _arrivingName, NetDevice.CodecName(_askedCodec),
                _arrivedAny && _arrivingName != NetDevice.CodecName(_askedCodec) &&
                NowNs() - Volatile.Read(ref _askedCodecAtNs) > 1_500_000_000);
        }
    }

    /// <summary>Starts keeping every frame's figures, for a measurement that needs percentiles.</summary>
    public void StartRecording(int capacity)
    {
        lock (_statsGate)
        {
            _logEncode = new double[capacity];
            _logNetwork = new double[capacity];
            _logDecode = new double[capacity];
            _logKb = new double[capacity];
            _logCount = 0;
        }
    }

    public NetRecording? Recording()
    {
        lock (_statsGate)
        {
            if (_logEncode is null) return null;
            return new NetRecording(_logEncode[.._logCount], _logNetwork![.._logCount],
                                    _logDecode![.._logCount], _logKb![.._logCount]);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }
}
