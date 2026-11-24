using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Rayxel.Diagnostics;
using Rayxel.Link.Discovery;
using Rayxel.Link.Security;
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
/// <para>
/// That is the old kind, named by address and spoken to in the clear. A sender paired with
/// this computer is <c>rxl://id</c> instead: found on the network by its id wherever its
/// address has moved to, and every session with it proven and sealed (see
/// <see cref="NetLinks"/>, <see cref="NetLink"/>).
/// </para>
/// </summary>
public static class NetDevice
{
    public const string Scheme = "net://";

    /// <summary>A paired sender's device path: rxl:// and its id.</summary>
    public const string LinkScheme = "rxl://";

    /// <summary>How <see cref="DeviceHardwareId.Instance"/> marks a paired sender: link: and its id.</summary>
    internal const string LinkInstance = "link:";

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

    /// <summary>The device record for a sender this computer is paired with.</summary>
    internal static CaptureDeviceInfo CreateLinked(Peer peer, int ordinal) =>
        new(ordinal, peer.Name, $"{LinkScheme}{peer.Id}");

    /// <summary>The paired sender a device stands for, or null for any other device.</summary>
    internal static Peer? PeerOf(CaptureDeviceInfo device) =>
        device.Bus == DeviceBus.Network && device.Hardware.Instance.StartsWith(LinkInstance, StringComparison.Ordinal)
            ? NetLinks.Store.Find(device.Hardware.Instance[LinkInstance.Length..])
            : null;

    // Where each paired sender was last found, for a few seconds: a probe and the stream that
    // follows it would otherwise both wait for the network to answer the same question.
    private static readonly Dictionary<string, (FoundSender? Found, long AtMs)> Located = [];

    /// <summary>
    /// Finds a paired sender on the network: a broadcast for its id, and its last address asked
    /// directly as well, since some routers keep broadcasts from crossing between Wi-Fi clients.
    /// Null when it does not answer.
    /// </summary>
    internal static FoundSender? Locate(Peer peer, bool fresh = false)
    {
        lock (Located)
        {
            if (!fresh && Located.TryGetValue(peer.Id, out var cached) && Environment.TickCount64 - cached.AtMs < 5000)
                return cached.Found;
        }

        var alsoAsk = IPEndPoint.TryParse(peer.LastAddress, out var last) ? new[] { last } : [];
        FoundSender? found;
        using (var discovery = new DiscoveryClient(NetLinks.Identity.Id))
            found = discovery.FindAsync(peer.Id, TimeSpan.FromMilliseconds(1500), alsoAsk).GetAwaiter().GetResult();

        if (found is not null)
        {
            NetLinks.Store.Seen(peer.Id, found.EndPoint.ToString());
            if (found.Name.Length > 0 && found.Name != peer.Name) NetLinks.Store.Rename(peer.Id, found.Name);
        }
        lock (Located) Located[peer.Id] = (found, Environment.TickCount64);
        return found;
    }

    /// <summary>
    /// Where a network device lives. False, with the reason, when the name will not resolve —
    /// a state to show, like a card that will not open, rather than an exception.
    /// </summary>
    public static bool TryGetEndPoint(CaptureDeviceInfo device, out IPEndPoint endPoint, out string problem)
    {
        endPoint = new IPEndPoint(IPAddress.None, 0);
        problem = "";

        if (device.Bus == DeviceBus.Network && device.Hardware.Instance.StartsWith(LinkInstance, StringComparison.Ordinal))
        {
            if (PeerOf(device) is not { } peer)
            {
                problem = Ui.Strings.T("deckForgotten");
                return false;
            }
            if (Locate(peer) is { } found)
            {
                endPoint = found.EndPoint;
                return true;
            }
            problem = Ui.Strings.T("deckNotFound");
            return false;
        }

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

    // Paired senders that were last found to have forgotten this computer: their row in the
    // device list opens pairing instead of trying to connect.
    private static readonly HashSet<string> ForgottenIds = [];

    /// <summary>
    /// True when the paired sender a device stands for was last found to no longer know this
    /// computer: connecting is refused until it is paired again.
    /// </summary>
    internal static bool Forgot(CaptureDeviceInfo device)
    {
        if (PeerOf(device) is not { } peer) return false;
        lock (ForgottenIds) return ForgottenIds.Contains(peer.Id);
    }

    private static void NoteForgotten(string id, bool forgot)
    {
        lock (ForgottenIds)
        {
            if (forgot) ForgottenIds.Add(id);
            else ForgottenIds.Remove(id);
        }
    }

    /// <summary>
    /// A device's name as lists show it: its own, and where two share it - two Decks are both
    /// "Steam Deck" until one is renamed - what tells them apart: a sender's last address, or
    /// for anything else its place in the list.
    /// </summary>
    internal static string DisplayName(CaptureDeviceInfo device, IReadOnlyList<CaptureDeviceInfo> all)
    {
        bool shared = all.Any(d => !d.Is(device) && string.Equals(d.FriendlyName, device.FriendlyName, StringComparison.CurrentCultureIgnoreCase));
        if (!shared) return device.FriendlyName;
        if (PeerOf(device) is { } peer)
        {
            string where = IPEndPoint.TryParse(peer.LastAddress, out var at) ? at.Address.ToString() : peer.Id[..6];
            return $"{device.FriendlyName} · {where}";
        }
        return $"{device.FriendlyName} ({device.Ordinal + 1})";
    }

    /// <summary>
    /// Asks the sender whether it is there, and describes what it offers. Costs the Deck
    /// nothing: it answers a ping without starting to capture.
    /// </summary>
    public static DeviceProbe Probe(CaptureDeviceInfo device)
    {
        // A paired sender answers a broadcast for it, and says whether it is switched on and
        // whether it still knows this computer.
        if (PeerOf(device) is { } peer)
        {
            var found = Locate(peer, fresh: true);
            Link.Wire.RefuseReason? refused = null;

            // A sender that has forgotten this computer answers the broadcast only while it
            // may be found. Asked directly at its last address it says why it will not talk -
            // which is the difference between "gone" and "pair again".
            if (found is null && IPEndPoint.TryParse(peer.LastAddress, out var last))
                (found, refused) = AskDirectly(peer, last);

            string? trouble = null;
            if (refused is Link.Wire.RefuseReason.NotPaired or Link.Wire.RefuseReason.BadProof
                || found is { KnowsYou: false })
                trouble = Ui.Strings.T("deckForgot");
            else if (refused is Link.Wire.RefuseReason.Disabled
                     || found is not null && !found.Flags.HasFlag(Link.Wire.AnnounceFlags.Enabled))
                trouble = Ui.Strings.T("deckOff");
            else if (found is null)
                trouble = Ui.Strings.T("deckNotFound");

            NoteForgotten(peer.Id, trouble == Ui.Strings.T("deckForgot"));
            if (trouble is not null)
            {
                Log.Info($"{peer.Name}: {trouble}" + (peer.LastAddress.Length > 0 ? $" (last seen at {peer.LastAddress})" : ""));
                return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown, trouble);
            }
            Log.Info($"{peer.Name} found at {found!.EndPoint}" + (found.Streaming ? ", streaming" : ""));
            return new DeviceProbe(device, DeviceAvailability.Ready, Modes(found.Model), LinkBudget.Unknown, null);
        }

        if (PeerOf(device) is null && device.Hardware.Instance.StartsWith(LinkInstance, StringComparison.Ordinal))
            return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown, Ui.Strings.T("deckForgotten"));

        if (!TryGetEndPoint(device, out var endPoint, out string problem))
            return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown, problem);

        double roundTripMs = Ping(endPoint);
        if (roundTripMs < 0)
            return new DeviceProbe(device, DeviceAvailability.Unreadable, [], LinkBudget.Unknown,
                $"no answer from {endPoint} - is `rayxel-send --listen` running on the Deck?");

        Log.Info($"sender at {endPoint} answered in {roundTripMs:0.0} ms");
        return new DeviceProbe(device, DeviceAvailability.Ready, Modes("Steam Deck"), LinkBudget.Unknown, null);
    }

    /// <summary>
    /// The sizes worth asking a Deck for. gamescope only ever scales its picture down, so
    /// these are its own 1280×800 and two smaller ones that encode faster and survive a
    /// poor link better. What actually arrives is stated in every frame, and that — not this
    /// list — is what the app reports once the stream is running: a docked Deck has a
    /// larger picture than any of these and simply sends the nearest fit.
    /// </summary>
    private static List<CaptureMode> Modes(string model)
    {
        var nv12 = VideoFormat.FromSubtype(VideoFormatGuids.NV12);
        // A Deck's own picture is 1280x800, and gamescope only scales down. A PC's desktop
        // is whatever its display is; a sender asked for more than it has sends its own size,
        // which the stream then states, so the larger sizes are safe to offer.
        (int Width, int Height)[] sizes = model == "Steam Deck"
            ? [(1280, 800), (960, 600), (640, 400)]
            : [(3840, 2160), (2560, 1440), (1920, 1080), (1280, 720)];

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

    /// <summary>
    /// Begins a session with a paired sender at its last address and lets it lapse: what the
    /// sender answers says whether it is there, switched on and still paired with this
    /// computer. Nothing is streamed - a sender starts only when the session is greeted.
    /// Found, when it took the session or was merely busy; and the refusal, when it gave one.
    /// </summary>
    private static (FoundSender? Found, Link.Wire.RefuseReason? Refused) AskDirectly(Peer peer, IPEndPoint at)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(at);
            uint session = (uint)Random.Shared.Next(1, int.MaxValue);
            using (ViewerHandshake.Connect(socket, NetLinks.Identity, peer, session, timeoutMs: 700)) { }
            return (Remember(peer, at, Link.Wire.AnnounceFlags.Enabled), null);
        }
        catch (LinkRefusedException ex) when (ex.Reason is Link.Wire.RefuseReason.Busy)
        {
            return (Remember(peer, at, Link.Wire.AnnounceFlags.Enabled | Link.Wire.AnnounceFlags.Streaming), null);
        }
        catch (LinkRefusedException ex)
        {
            return (null, ex.Reason);
        }
        catch (Exception ex)
        {
            // Nobody there, or a key kept here that will not compute: either way not found.
            Log.Debug($"asking {peer.Name} at {at} directly failed: {ex.Message}");
            return (null, null);
        }

        // It answered as the one paired with: kept where it was found, like a broadcast's answer.
        static FoundSender Remember(Peer peer, IPEndPoint at, Link.Wire.AnnounceFlags flags)
        {
            var found = new FoundSender(at, peer.Id, peer.Name, peer.Model, flags | Link.Wire.AnnounceFlags.KnowsYou,
                                        byte.MaxValue, Environment.TickCount64);
            lock (Located) Located[peer.Id] = (found, Environment.TickCount64);
            return found;
        }
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
                socket.Send(ping[..NetWire.WritePing(ping, 0, sent, NetWire.Version1)]);

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
