using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rayxel.Diagnostics;
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
public sealed record NetStats(
    double EncodeMs, double NetworkMs, double DecodeMs, double RoundTripMs, bool ClockKnown, bool DecodeKnown,
    long FramesLost, long PacketsLate, long KeyframesRequested, double Mbps, string Decoder);

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
internal sealed class NetFeed : IDisposable, HevcDecoder.ISink
{
    private readonly IPEndPoint _sender;
    private readonly int _width, _height, _fps, _bitrateKbps;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly INetSink _sink;
    private readonly Func<bool> _focused;
    private volatile DeskForwarder? _desk;
    private readonly uint _session = (uint)Random.Shared.Next(1, int.MaxValue);

    private Thread? _thread;
    private volatile bool _running;
    private HevcDecoder? _decoder;
    private (int Width, int Height) _decoderSize;

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

    public NetFeed(IPEndPoint sender, int width, int height, int fps, int bitrateKbps,
                   IMFDXGIDeviceManager deviceManager, INetSink sink, Func<bool> focused)
    {
        _focused = focused;
        _sender = sender;
        _width = width;
        _height = height;
        _fps = fps;
        _bitrateKbps = bitrateKbps;
        _deviceManager = deviceManager;
        _sink = sink;
    }

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
            // Room for several frames. While one is being decoded nobody is reading, and
            // whatever arrives in that time has to wait here rather than be dropped.
            socket.ReceiveBufferSize = 8 << 20;
            socket.Connect(_sender);

            // The controller goes the other way down the same connection, on a thread of
            // its own: it has to be looked at a thousand times a second, and this one
            // spends its time waiting for packets.
            using var pad = new PadForwarder(socket, _session, _focused);
            pad.Start();

            // And the keyboard and mouse, once the user hands them over.
            using var desk = new DeskForwarder(socket, _session, _focused);
            desk.Start();
            _desk = desk;

            byte[] packet = new byte[2048];
            byte[] outgoing = new byte[32];
            long nextHello = 0, nextPing = 0;
            int pingsSent = 0;
            _windowStartNs = NowNs();

            Log.Info($"asking {_sender} for {_width}x{_height}, {_bitrateKbps / 1000.0:0.#} Mbit/s at {_fps} fps");

            while (_running)
            {
                long now = NowNs();
                if (now >= nextHello)
                {
                    socket.Send(outgoing, NetWire.WriteHello(outgoing, _session, _width, _height, _fps, _bitrateKbps), SocketFlags.None);
                    nextHello = now + 500_000_000;
                }
                if (now >= nextPing)
                {
                    socket.Send(outgoing, NetWire.WritePing(outgoing, now), SocketFlags.None);
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

                var data = packet.AsSpan(0, n);
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
                            OnPacket(socket, header, data[NetWire.VideoHeaderSize..], arrived);
                        break;
                }
            }

            pad.Dispose();
            _desk = null;
            desk.Dispose();
            try { socket.Send(outgoing, NetWire.WriteSession(outgoing, NetWire.Bye, _session), SocketFlags.None); }
            catch (SocketException) { }
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
            socket?.Dispose();
        }
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

    private void OnPacket(Socket socket, in NetWire.VideoHeader h, ReadOnlySpan<byte> payload, long arrived)
    {
        // Left over from a frame already given up on - unless the sender has started over.
        // It does when it is restarted, or when it stopped hearing from us for a while:
        // its count begins again from nothing, and everything it sends from then on looks
        // old. A keyframe with an old number, or a number far behind, is a new beginning
        // and not a straggler; taking it for one left the picture gone for good.
        if (_hasFrame && (int)(h.Frame - _head.Frame) < 0)
        {
            bool startedOver = h.Keyframe || (int)(_head.Frame - h.Frame) > 60;
            if (!startedOver)
            {
                Interlocked.Increment(ref _packetsLate);
                return;
            }
            _hasFrame = false;
            _waitingForKeyframe = true;
        }

        if (!_hasFrame || h.Frame != _head.Frame)
        {
            // A newer frame has started. The one before it is lost if it never became
            // whole, and so is every frame none of whose packets arrived; either way what
            // follows refers to pictures we do not have, until the next keyframe.
            if (_hasFrame)
            {
                long missing = (_got < _head.Count ? 1 : 0) + (long)(h.Frame - _head.Frame - 1);
                if (missing > 0)
                {
                    Interlocked.Add(ref _framesLost, missing);
                    _sink.NetLost(missing);
                    _waitingForKeyframe = true;
                }
            }

            if (h.Count <= 0 || h.Count > NetWire.MaxPackets || h.FrameBytes <= 0 ||
                h.FrameBytes > h.Count * NetWire.PayloadMax)
                return;

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

        // The frame is whole.
        _completedNs = arrived;
        if (_head.Keyframe) _waitingForKeyframe = false;
        if (_waitingForKeyframe)
        {
            AskForKeyframe(socket, arrived);
            return;
        }
        DecodeFrame(socket, arrived);
    }

    /// <summary>At most a few times a second: the answer takes a frame or two to arrive, and asking again in the meantime changes nothing.</summary>
    private void AskForKeyframe(Socket socket, long now)
    {
        if (now - _keyframeAskedNs < 250_000_000) return;
        _keyframeAskedNs = now;
        Interlocked.Increment(ref _keyframesRequested);

        Span<byte> request = stackalloc byte[8];
        try { socket.Send(request[..NetWire.WriteSession(request, NetWire.Keyframe, _session)]); }
        catch (SocketException) { }
    }

    private void DecodeFrame(Socket socket, long arrived)
    {
        try
        {
            if (_decoder is null || _decoderSize != (_head.Width, _head.Height))
            {
                _decoder?.Dispose();
                _decoder = null;
                _decoder = new HevcDecoder(_deviceManager, _head.Width, _head.Height);
                _decoderSize = (_head.Width, _head.Height);
                Log.Info($"decoding {_head.Width}x{_head.Height} HEVC with '{_decoder.Name}'");
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
            AskForKeyframe(socket, arrived);
        }
    }

    void HevcDecoder.ISink.OnPicture(ID3D11Texture2D texture, int subresource, long sampleTime)
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
                Interlocked.Read(ref _keyframesRequested), _mbps, _decoder?.Name ?? "-");
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
