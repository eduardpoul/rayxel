using System.Net.Sockets;

namespace Rayxel.Link.Security;

/// <summary>Why a session could not be had: what the sender said, or that it said nothing.</summary>
public sealed class LinkRefusedException(string message, Wire.RefuseReason? reason = null) : Exception(message)
{
    public Wire.RefuseReason? Reason { get; } = reason;
}

/// <summary>
/// The viewer's half of starting a session with a paired sender: CONNECT with a fresh nonce,
/// WELCOME back with the sender's nonce and proof that it holds the key that was paired, and
/// from the two nonces the session's keys. Everything after travels sealed.
/// </summary>
public static class ViewerHandshake
{
    /// <summary>
    /// Runs the handshake over <paramref name="socket"/>, which is connected to the sender.
    /// Resends CONNECT every 300 ms. Returns the channel to seal and open with; throws
    /// <see cref="LinkRefusedException"/> when the sender refuses, cannot prove itself, or
    /// does not answer within <paramref name="timeoutMs"/>.
    /// </summary>
    public static SecureChannel Connect(Socket socket, Identity identity, Peer sender, uint session, int timeoutMs = 3000)
    {
        byte[] senderPublicKey = sender.PublicKeyBytes;
        byte[] linkSecret = LinkKeys.LinkSecret(identity, senderPublicKey, identity.PublicKey, senderPublicKey);
        byte[] viewerNonce = LinkKeys.Random(Wire.NonceSize);

        var request = new byte[64];
        int length = Wire.WriteConnect(request, session, identity.Id, viewerNonce);
        var buffer = new byte[Wire.DatagramMax];

        long deadline = Clock.NowNs + timeoutMs * 1_000_000L, nextSend = 0;
        while (Clock.NowNs < deadline)
        {
            if (Clock.NowNs >= nextSend)
            {
                try { socket.Send(request.AsSpan(0, length)); } catch (SocketException) { }
                nextSend = Clock.NowNs + 300_000_000;
            }
            if (!socket.Poll(20_000, SelectMode.SelectRead)) continue;
            int n = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None, out SocketError error);
            if (error != SocketError.Success || n <= 0) continue; // an ICMP "nobody here" from a sender not yet started

            var data = buffer.AsSpan(0, n);
            byte type = Wire.TypeOf(data);
            if (Wire.NumberOf(data) != session) continue;

            if (type == Wire.Refuse && Wire.TryReadRefuse(data, out _, out var reason))
                throw new LinkRefusedException(Describe(reason), reason);

            if (type != Wire.Welcome || !Wire.TryReadWelcome(data, out _, out var senderId, out var senderNonce, out var proof))
                continue;
            if (!LinkKeys.Equal(senderId, sender.IdBytes))
                throw new LinkRefusedException("another sender answered at that address");

            byte[] expected = LinkKeys.Proof(linkSecret, "welcome", viewerNonce, senderNonce.ToArray(), LinkKeys.U32(session));
            if (!LinkKeys.Equal(proof, expected))
                throw new LinkRefusedException("the sender could not prove it is the one paired with", Wire.RefuseReason.BadProof);

            var (toSender, toViewer) = LinkKeys.SessionKeys(linkSecret, viewerNonce, senderNonce, session);
            return new SecureChannel(toSender, toViewer);
        }
        throw new LinkRefusedException("the sender did not answer");
    }

    public static string Describe(Wire.RefuseReason reason) => reason switch
    {
        Wire.RefuseReason.NotPaired => "the sender does not know this computer: pair again",
        Wire.RefuseReason.Busy => "another computer is watching",
        Wire.RefuseReason.Disabled => "the sender is switched off",
        Wire.RefuseReason.BadProof => "the keys did not match: pair again",
        _ => $"refused ({(byte)reason})"
    };
}
