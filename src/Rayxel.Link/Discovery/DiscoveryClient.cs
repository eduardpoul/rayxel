using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Rayxel.Link.Discovery;

/// <summary>A sender that answered: where it is and what it says of itself.</summary>
public sealed record FoundSender(
    IPEndPoint EndPoint, string Id, string Name, string Model, Wire.AnnounceFlags Flags, byte CodecMask, long SeenMs)
{
    public bool Discoverable => Flags.HasFlag(Wire.AnnounceFlags.Discoverable);
    public bool KnowsYou => Flags.HasFlag(Wire.AnnounceFlags.KnowsYou);
    public bool Streaming => Flags.HasFlag(Wire.AnnounceFlags.Streaming);

    public bool CanEncode(Wire.Codec codec) => (CodecMask & (1 << (int)codec)) != 0;
}

/// <summary>
/// Finds senders on the local network: a DISCOVER broadcast on the sender's port, and the
/// ANNOUNCE every sender sends back if it is open to pairing or already knows this viewer.
/// <para>
/// mDNS would be the textbook way, but a Steam Deck runs no Avahi, so the senders answer for
/// themselves. A broadcast goes to 255.255.255.255 and to each network's own broadcast
/// address, since Windows sends the first out of one interface only. Some routers keep
/// broadcasts from crossing between Wi-Fi clients; a sender's last known address is tried
/// directly as well, and an address can always be typed.
/// </para>
/// </summary>
public sealed class DiscoveryClient : IDisposable
{
    private readonly byte[] _viewerId;
    private readonly int _port;
    private readonly Socket _socket;

    public DiscoveryClient(byte[] viewerId, int port = Wire.DefaultPort)
    {
        _viewerId = viewerId;
        _port = port;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>Every sender that answers within <paramref name="duration"/>, asking again every 300 ms.</summary>
    public async Task<IReadOnlyList<FoundSender>> ScanAsync(TimeSpan duration, IEnumerable<IPEndPoint>? alsoAsk = null,
                                                            CancellationToken cancel = default)
    {
        var found = new Dictionary<string, FoundSender>();
        await RunAsync(duration, alsoAsk, sender =>
        {
            found[sender.Id] = sender;
            return false;
        }, cancel);
        return found.Values.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The sender with this id, if it answers within <paramref name="timeout"/>.</summary>
    public async Task<FoundSender?> FindAsync(string senderId, TimeSpan timeout, IEnumerable<IPEndPoint>? alsoAsk = null,
                                              CancellationToken cancel = default)
    {
        FoundSender? result = null;
        await RunAsync(timeout, alsoAsk, sender =>
        {
            if (sender.Id != senderId) return false;
            result = sender;
            return true;
        }, cancel);
        return result;
    }

    /// <summary>Asks repeatedly and hands each answer to <paramref name="onFound"/> until it returns true or time runs out.</summary>
    private async Task RunAsync(TimeSpan duration, IEnumerable<IPEndPoint>? alsoAsk, Func<FoundSender, bool> onFound,
                                CancellationToken cancel)
    {
        uint query = (uint)Random.Shared.Next(1, int.MaxValue);
        var request = new byte[64];
        int length = Wire.WriteDiscover(request, query, _viewerId);
        var targets = BroadcastAddresses().Select(a => new IPEndPoint(a, _port)).Concat(alsoAsk ?? []).Distinct().ToList();

        var buffer = new byte[Wire.DatagramMax];
        long end = Clock.NowNs + (long)duration.TotalMilliseconds * 1_000_000;
        long nextAsk = 0;
        while (Clock.NowNs < end && !cancel.IsCancellationRequested)
        {
            if (Clock.NowNs >= nextAsk)
            {
                foreach (var target in targets)
                {
                    try { _socket.SendTo(request.AsSpan(0, length), SocketFlags.None, target); }
                    catch (SocketException) { /* a network that cannot take a broadcast just does not get one */ }
                }
                nextAsk = Clock.NowNs + 300_000_000;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(nextAsk, end) - Clock.NowNs) / 1_000_000.0));
            SocketReceiveFromResult received;
            try
            {
                received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token);
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (SocketException)
            {
                continue;
            }

            var data = buffer.AsSpan(0, received.ReceivedBytes);
            if (Wire.TypeOf(data) != Wire.Announce || Wire.NumberOf(data) != query) continue;
            if (!Wire.TryReadAnnounce(data, out var announce)) continue;
            var from = (IPEndPoint)received.RemoteEndPoint;
            var sender = new FoundSender(new IPEndPoint(from.Address, announce.Port), Security.Identity.Hex(announce.SenderId),
                                         announce.Name, announce.Model, announce.Flags, announce.CodecMask, Clock.UnixMs);
            if (onFound(sender)) return;
        }
    }

    /// <summary>255.255.255.255 and the broadcast address of every IPv4 network this machine is on.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    byte[] ip = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    addresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { }
        return addresses;
    }

    public void Dispose() => _socket.Dispose();
}
