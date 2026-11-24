using System.Net.Sockets;
using Rayxel.Link.Security;

namespace Rayxel.Capture;

/// <summary>
/// The viewer's side of one connection to a sender: the socket, and for a paired sender the
/// session's sealing. Everything the viewer sends - HELLO, PING, KEYFRAME, BYE, and the
/// controller, keyboard and mouse from their own threads - goes through <see cref="Send"/>,
/// written in the plain layout with <see cref="Version"/>; what arrives is opened by
/// <see cref="Open"/> into the same plain layout. A sender named by address on the command
/// line is the old kind, unpaired, and speaks version 1 in the clear.
/// </summary>
internal sealed class NetLink
{
    private volatile SecureChannel? _channel;

    [ThreadStatic] private static byte[]? _sealBuffer;

    public NetLink(Socket socket, bool secure)
    {
        Socket = socket;
        Secure = secure;
    }

    public Socket Socket { get; }

    /// <summary>A paired sender: nothing goes out until the handshake has given a channel, and nothing unsealed is taken in.</summary>
    public bool Secure { get; }

    public byte Version => Secure ? NetWire.Version2 : NetWire.Version1;

    /// <summary>
    /// The session's channel. Replaced when a session is begun again; the old one is left to
    /// the collector rather than disposed, since another thread may be sealing with it.
    /// </summary>
    public SecureChannel? Channel
    {
        get => _channel;
        set => _channel = value;
    }

    /// <summary>Whether messages can go out: always for the old kind, once the handshake is done for a paired one.</summary>
    public bool Ready => !Secure || _channel is not null;

    /// <summary>Sends one message, sealed when the link is. Safe from any thread; a failed send is dropped like a lost datagram.</summary>
    public void Send(ReadOnlySpan<byte> plain)
    {
        try
        {
            if (!Secure)
            {
                Socket.Send(plain);
                return;
            }
            if (_channel is not { } channel) return;
            var buffer = _sealBuffer ??= new byte[NetWire.DatagramMax];
            Socket.Send(buffer.AsSpan(0, channel.Seal(plain, buffer)));
        }
        catch (SocketException) { }
    }

    /// <summary>
    /// What arrived, ready to read: opened into <paramref name="plain"/> when sealed, as it
    /// is otherwise. Empty when it is not for this link - something unsealed on a paired
    /// link, or sealed by anyone but this session's sender. REFUSE comes through unsealed.
    /// </summary>
    public ReadOnlySpan<byte> Open(ReadOnlySpan<byte> datagram, byte[] plain)
    {
        byte type = NetWire.TypeOf(datagram);
        if (!Secure) return NetWire.VersionOf(datagram) == NetWire.Version1 ? datagram : default;
        if (type == NetWire.Refuse) return datagram;
        if (!NetWire.IsSealed(type) || _channel is not { } channel) return default;
        return channel.Open(datagram, plain, out int length) ? plain.AsSpan(0, length) : default;
    }
}
