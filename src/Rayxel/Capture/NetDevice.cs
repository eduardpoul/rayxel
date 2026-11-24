using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rayxel.Diagnostics;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>
/// A Steam Deck running <c>rayxel-send</c>, presented as a capture device.
/// <para>
/// It is deliberately not a separate kind of thing. To the rest of the app a source is a
/// device with a list of modes, one of which is running, and a Deck on the network fits
/// that exactly: its modes are the sizes it can be asked for, and the codec in its path is
/// said out loud the same way a card's is. The interface path that identifies a card is,
/// for one of these, <c>net://host:port</c>.
/// </para>
/// </summary>
public static class NetDevice
{
    public const string Scheme = "net://";

    /// <summary>
    /// What the sender is asked to spend when the bitrate is fixed, in kbit/s. One figure
    /// for every size.
    /// </summary>
    public static int BitrateKbps { get; set; } = 20_000;

    /// <summary>
    /// True when the bitrate follows the link instead: see <see cref="BitrateGovernor"/>.
    /// It goes no higher than <see cref="AdaptiveCeilingKbps"/>. Read by the stream while
    /// it runs, so a change takes effect at once.
    /// </summary>
    public static bool Adaptive { get; set; } = true;

    /// <summary>
    /// The most an adaptive bitrate asks for, and what it starts at. A picture of the Deck's
    /// size is as good as it gets well below this; more only makes each frame a longer
    /// burst on the air.
    /// </summary>
    public const int AdaptiveCeilingKbps = 20_000;

    /// <summary>The fixed rates offered beside the adaptive one, in kbit/s.</summary>
    public static readonly int[] FixedKbps = [30_000, 20_000, 15_000, 10_000, 5_000];

    /// <summary>
    /// The codec the Deck is asked for. HEVC unless chosen otherwise: every Deck encodes it.
    /// AV1 needs an encoder a Deck's GPU does not have, and a sender that cannot do what is
    /// asked sends HEVC instead and says so in every frame. Read by the stream while it
    /// runs, so a change takes effect at once, like the bitrate.
    /// </summary>
    internal static NetWire.Codec Codec { get; set; } = NetWire.Codec.Hevc;

    /// <summary>
    /// What PyroWave is asked to spend, in kbit/s. Its own figure, because it is a different
    /// order of thing: every frame is compressed alone, so it needs several times HEVC's
    /// rate for the same picture. Always fixed — it is meant for a link that has the room.
    /// </summary>
    public static int PyroWaveKbps { get; set; } = 140_000;

    /// <summary>PyroWave's rates to choose from, in kbit/s. 140 looks like HEVC at 20.</summary>
    public static readonly int[] PyroWaveChoicesKbps = [200_000, 140_000, 100_000, 70_000];

    internal static string CodecName(NetWire.Codec codec) => codec switch
    {
        NetWire.Codec.Av1 => "AV1",
        NetWire.Codec.PyroWave => "PyroWave",
        _ => "HEVC"
    };

    /// <summary>Makes the device record for a sender at <c>host</c> or <c>host:port</c>.</summary>
    public static CaptureDeviceInfo Create(string address, int ordinal)
    {
        string host = address.Trim();
        int port = NetWire.DefaultPort;

        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out int given) && given is > 0 and < 65536)
        {
            port = given;
            host = host[..colon];
        }

        return new CaptureDeviceInfo(ordinal, $"Steam Deck at {host}", $"{Scheme}{host}:{port}");
    }

    /// <summary>
    /// Where a network device lives. False, with the reason, when the name will not resolve —
    /// a state to show, like a card that will not open, rather than an exception.
    /// </summary>
    public static bool TryGetEndPoint(CaptureDeviceInfo device, out IPEndPoint endPoint, out string problem)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);
        problem = "";

        string address = device.Hardware.Instance;
        int colon = address.LastIndexOf(':');
        if (device.Bus != DeviceBus.Network || colon <= 0 || !int.TryParse(address[(colon + 1)..], out int port))
        {
            problem = $"'{device.SymbolicLink}' is not a network address";
            return false;
        }

        string host = address[..colon];
        try
        {
            var ip = IPAddress.TryParse(host, out var literal)
                ? literal
                : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                problem = $"'{host}' has no IPv4 address";
                return false;
            }
            endPoint = new IPEndPoint(ip, port);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"'{host}' could not be found: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Asks the sender whether it is there, and describes what it offers. Costs the Deck
    /// nothing: it answers a ping without starting to capture.
    /// </summary>
    public static DeviceProbe Probe(CaptureDeviceInfo device)
    {
        if (!TryGetEndPoint(device, out var endPoint, out string problem))
            return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown, problem);

        double roundTripMs = Ping(endPoint);
        if (roundTripMs < 0)
            return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown,
                $"no answer from {endPoint} - is `rayxel-send --listen` running on the Deck?");

        Log.Info($"sender at {endPoint} answered in {roundTripMs:0.0} ms");
        return new DeviceProbe(device, DeviceAvailability.Ready, Modes(), LinkBudget.Unknown, null);
    }

    /// <summary>
    /// The sizes worth asking a Deck for. gamescope only ever scales its picture down, so
    /// these are its own 1280×800 and two smaller ones that encode faster and survive a
    /// poor link better. What actually arrives is stated in every frame, and that — not this
    /// list — is what the app reports once the stream is running: a docked Deck has a
    /// larger picture than any of these and simply sends the nearest fit.
    /// </summary>
    private static List<CaptureMode> Modes()
    {
        var nv12 = VideoFormat.FromSubtype(VideoFormatGuids.NV12);
        (int Width, int Height)[] sizes = [(1280, 800), (960, 600), (640, 400)];

        var modes = new List<CaptureMode>();
        for (int i = 0; i < sizes.Length; i++)
        {
            // NV12 is what the app is handed, after its own decode; the codec on the wire is
            // declared as such, so the mode carries the same "zip" tag a card's compressed
            // modes do. It is the one asked for; once frames arrive, the one they say.
            modes.Add(new CaptureMode(i, nv12, sizes[i].Width, sizes[i].Height, 60, 60, 60)
            {
                Transport = WireTransport.Compressed,
                WireCodec = CodecName(Codec)
            });
        }
        return modes;
    }

    /// <summary>Round trip to the sender in milliseconds, or -1 when it does not answer.</summary>
    private static double Ping(IPEndPoint endPoint)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(endPoint);

            Span<byte> ping = stackalloc byte[16];
            byte[] reply = new byte[64];

            for (int attempt = 0; attempt < 4; attempt++)
            {
                long sent = Stopwatch.GetTimestamp();
                socket.Send(ping[..NetWire.WritePing(ping, sent)]);

                // A port nobody listens on answers with an ICMP error, which Windows turns
                // into a failed receive; that is a "no" as well, just a faster one.
                if (!socket.Poll(300_000, SelectMode.SelectRead)) continue;
                int n = socket.Receive(reply, 0, reply.Length, SocketFlags.None, out SocketError error);
                if (error != SocketError.Success) continue;
                if (NetWire.TypeOf(reply.AsSpan(0, n)) == NetWire.Pong)
                    return Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            }
        }
        catch (SocketException ex)
        {
            Log.Debug($"ping to {endPoint} failed: {ex.SocketErrorCode}");
        }
        return -1;
    }
}
