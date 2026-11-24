using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Rayxel.Link.Security;

/// <summary>
/// Pairing: how a viewer and a sender come to hold each other's public keys, with a person
/// confirming that nobody stood in between.
/// <para>
/// A PIN cannot simply be a password both sides check: six digits are a million guesses,
/// and anyone who recorded the exchange could try them all at leisure. So the PIN here is not
/// a secret at all - it is a fingerprint of the exchange itself, which the sender shows and
/// the person types into the viewer to say "these are the same":
/// </para>
/// <list type="number">
/// <item>PAIR_REQUEST: the viewer commits to its key - sends a hash of its public key and a nonce, not the key.</item>
/// <item>PAIR_KEY: the sender answers with its public key and a nonce of its own.</item>
/// <item>PAIR_REVEAL: only now the viewer sends its key and nonce, which must match the commitment.</item>
/// <item>Both compute the PIN from both keys and both nonces; the sender shows it, the person types it into the viewer, the viewer compares.</item>
/// <item>PAIR_CONFIRM, PAIR_DONE: each proves it holds its private key by a MAC under the secret the two keys give, and both store the other.</item>
/// </list>
/// <para>
/// Whoever stands in the middle has to pick their own keys before seeing the other side's
/// nonce, so the PIN the two ends see agrees by chance one time in a million - and they get
/// one try, because each attempt starts over with fresh nonces. The viewer's own comparison
/// can be retried for a typo: that gives an attacker nothing. Nothing here travels secret;
/// it does not need to.
/// </para>
/// </summary>
public static class Pairing
{
    /// <summary>How long a pairing may take from the first message to the last.</summary>
    public const int TimeoutMs = 120_000;

    public enum CancelReason : byte
    {
        Cancelled = 1,   // someone pressed Cancel
        NotOpen = 2,     // the sender is not discoverable: pairing is closed
        Busy = 3,        // another pairing is under way
        BadReveal = 4,   // the key revealed does not match the commitment
        BadProof = 5,    // a MAC did not check out
        TimedOut = 6,
    }

    public static byte[] Commitment(ReadOnlySpan<byte> viewerPublicKey, ReadOnlySpan<byte> viewerNonce) =>
        SHA256.HashData([.. viewerPublicKey, .. viewerNonce]);

    public static byte[] Transcript(byte[] viewerId, byte[] senderId, byte[] viewerPublicKey, byte[] senderPublicKey,
                                    byte[] viewerNonce, byte[] senderNonce) =>
        SHA256.HashData([.. viewerId, .. senderId, .. viewerPublicKey, .. senderPublicKey, .. viewerNonce, .. senderNonce]);

    /// <summary>The six digits both ends show for one exchange.</summary>
    public static string Pin(byte[] transcript)
    {
        byte[] digest = SHA256.HashData([.. Encoding.ASCII.GetBytes("rayxel pin v2"), .. transcript]);
        return (BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000).ToString("D6");
    }

    // ---------------------------------------------------------------------------------
    // The messages. The 32-bit number at offset 4 is the pairing's, picked by the viewer.

    public static int WriteRequest(Span<byte> p, uint pairing, byte[] viewerId, byte[] commitment, string viewerName)
    {
        Wire.WriteHeader(p, Wire.PairRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], pairing);
        viewerId.CopyTo(p[8..]);
        commitment.CopyTo(p[24..]);
        return 56 + Wire.WriteName(p[56..], viewerName);
    }

    public static bool TryReadRequest(ReadOnlySpan<byte> p, out byte[] viewerId, out byte[] commitment, out string viewerName)
    {
        viewerId = commitment = [];
        viewerName = "";
        if (p.Length < 57 || !Wire.TryReadName(p[56..], out viewerName, out _)) return false;
        viewerId = p.Slice(8, Wire.IdSize).ToArray();
        commitment = p.Slice(24, Wire.CommitSize).ToArray();
        return true;
    }

    public static int WriteKey(Span<byte> p, uint pairing, byte[] senderId, byte[] senderPublicKey, byte[] senderNonce,
                               string senderName, string model)
    {
        Wire.WriteHeader(p, Wire.PairKey);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], pairing);
        senderId.CopyTo(p[8..]);
        senderPublicKey.CopyTo(p[24..]);
        senderNonce.CopyTo(p[89..]);
        int at = 105;
        at += Wire.WriteName(p[at..], senderName);
        at += Wire.WriteName(p[at..], model);
        return at;
    }

    public static bool TryReadKey(ReadOnlySpan<byte> p, out byte[] senderId, out byte[] senderPublicKey, out byte[] senderNonce,
                                  out string senderName, out string model)
    {
        senderId = senderPublicKey = senderNonce = [];
        senderName = model = "";
        if (p.Length < 107) return false;
        if (!Wire.TryReadName(p[105..], out senderName, out int used)) return false;
        if (!Wire.TryReadName(p[(105 + used)..], out model, out _)) return false;
        senderId = p.Slice(8, Wire.IdSize).ToArray();
        senderPublicKey = p.Slice(24, Wire.PublicKeySize).ToArray();
        senderNonce = p.Slice(89, Wire.NonceSize).ToArray();
        return true;
    }

    public static int WriteReveal(Span<byte> p, uint pairing, byte[] viewerPublicKey, byte[] viewerNonce)
    {
        Wire.WriteHeader(p, Wire.PairReveal);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], pairing);
        viewerPublicKey.CopyTo(p[8..]);
        viewerNonce.CopyTo(p[73..]);
        return 89;
    }

    public static bool TryReadReveal(ReadOnlySpan<byte> p, out byte[] viewerPublicKey, out byte[] viewerNonce)
    {
        viewerPublicKey = viewerNonce = [];
        if (p.Length < 89) return false;
        viewerPublicKey = p.Slice(8, Wire.PublicKeySize).ToArray();
        viewerNonce = p.Slice(73, Wire.NonceSize).ToArray();
        return true;
    }

    /// <summary>PAIR_CONFIRM and PAIR_DONE: a MAC and nothing else.</summary>
    public static int WriteProof(Span<byte> p, byte type, uint pairing, byte[] mac)
    {
        Wire.WriteHeader(p, type);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], pairing);
        mac.CopyTo(p[8..]);
        return 8 + Wire.MacSize;
    }

    public static bool TryReadProof(ReadOnlySpan<byte> p, out byte[] mac)
    {
        mac = p.Length >= 8 + Wire.MacSize ? p.Slice(8, Wire.MacSize).ToArray() : [];
        return mac.Length == Wire.MacSize;
    }

    public static int WriteCancel(Span<byte> p, uint pairing, CancelReason reason)
    {
        Wire.WriteHeader(p, Wire.PairCancel);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], pairing);
        p[8] = (byte)reason;
        return 9;
    }

    public static string Describe(CancelReason reason) => reason switch
    {
        CancelReason.Cancelled => "pairing was cancelled",
        CancelReason.NotOpen => "the sender is not open to pairing: make it discoverable first",
        CancelReason.Busy => "the sender is already pairing with someone",
        CancelReason.BadReveal => "the viewer's key did not match what it promised",
        CancelReason.BadProof => "the other side could not prove its key",
        CancelReason.TimedOut => "pairing took too long",
        _ => $"pairing ended ({(byte)reason})"
    };
}

/// <summary>
/// One pairing as the sender sees it, from PAIR_REQUEST to PAIR_DONE. Driven by the sender's
/// network thread; every step answers duplicates the same way, because the viewer resends
/// until it hears back.
/// </summary>
public sealed class SenderPairing
{
    public enum Stages { AwaitingReveal, ShowingPin, Done }

    public uint Number { get; }
    public string ViewerName { get; }
    public byte[] ViewerId { get; }
    public string FromAddress { get; }
    public long ExpiresMs { get; }
    public Stages Stage { get; private set; } = Stages.AwaitingReveal;

    /// <summary>The PIN to show, once the viewer has revealed its key.</summary>
    public string? Pin { get; private set; }

    private readonly Identity _identity;
    private readonly byte[] _commitment, _senderNonce = LinkKeys.Random(Wire.NonceSize);
    private byte[]? _viewerPublicKey, _transcript, _linkSecret;

    public SenderPairing(Identity identity, uint number, byte[] viewerId, byte[] commitment, string viewerName, string fromAddress)
    {
        _identity = identity;
        Number = number;
        ViewerId = viewerId;
        _commitment = commitment;
        ViewerName = viewerName.Length > 0 ? viewerName : "A computer";
        FromAddress = fromAddress;
        ExpiresMs = Clock.UnixMs + Pairing.TimeoutMs;
    }

    public bool Expired => Clock.UnixMs > ExpiresMs;

    /// <summary>PAIR_KEY, the answer to PAIR_REQUEST - the same each time it is asked for.</summary>
    public int WriteKey(Span<byte> p, string name, string model) =>
        Pairing.WriteKey(p, Number, _identity.Id, _identity.PublicKey, _senderNonce, name, model);

    /// <summary>PAIR_REVEAL: checks the key against the commitment and works out the PIN. Null on success, else why not.</summary>
    public Pairing.CancelReason? OnReveal(byte[] viewerPublicKey, byte[] viewerNonce)
    {
        if (Stage != Stages.AwaitingReveal)
            return _viewerPublicKey is not null && LinkKeys.Equal(viewerPublicKey, _viewerPublicKey) ? null : Pairing.CancelReason.BadReveal;

        if (!LinkKeys.Equal(Pairing.Commitment(viewerPublicKey, viewerNonce), _commitment) ||
            !LinkKeys.Equal(Identity.IdOf(viewerPublicKey), ViewerId) ||
            !Identity.IsValidPublicKey(viewerPublicKey))
            return Pairing.CancelReason.BadReveal;

        _viewerPublicKey = viewerPublicKey;
        _transcript = Pairing.Transcript(ViewerId, _identity.Id, viewerPublicKey, _identity.PublicKey, viewerNonce, _senderNonce);
        _linkSecret = LinkKeys.LinkSecret(_identity, viewerPublicKey, viewerPublicKey, _identity.PublicKey);
        Pin = Pairing.Pin(_transcript);
        Stage = Stages.ShowingPin;
        return null;
    }

    /// <summary>
    /// PAIR_CONFIRM: the person typed the right PIN into the viewer, which proves its key. The
    /// peer to store on success, null when the proof is wrong.
    /// </summary>
    public Peer? OnConfirm(byte[] mac)
    {
        if (_transcript is null || _linkSecret is null || _viewerPublicKey is null) return null;
        if (!LinkKeys.Equal(mac, LinkKeys.Proof(_linkSecret, "pair confirm", _transcript))) return null;
        Stage = Stages.Done;
        return new Peer
        {
            Id = Identity.Hex(ViewerId),
            Name = ViewerName,
            PublicKey = Convert.ToBase64String(_viewerPublicKey),
            PairedMs = Clock.UnixMs,
            LastSeenMs = Clock.UnixMs,
            LastAddress = FromAddress,
        };
    }

    /// <summary>PAIR_DONE, after a good PAIR_CONFIRM - again the same each time.</summary>
    public int WriteDone(Span<byte> p) =>
        Pairing.WriteProof(p, Wire.PairDone, Number, LinkKeys.Proof(_linkSecret!, "pair done", _transcript!));
}

/// <summary>What a sender said of itself when it answered a pairing request.</summary>
public sealed record PairingOffer(string SenderName, string Model, string SenderId);

/// <summary>
/// Pairing as the viewer does it, with a socket of its own:
/// <c>await StartAsync()</c> (the sender then shows a PIN), ask the person for it,
/// <c>await ConfirmAsync(pin)</c> (true: paired - store <see cref="Result"/>).
/// Every message is resent twice a second until the answer comes, because nothing on the
/// network is guaranteed to arrive.
/// </summary>
public sealed class PairingClient : IDisposable
{
    private readonly Identity _identity;
    private readonly string _viewerName;
    private readonly IPEndPoint _sender;
    private readonly Socket _socket;
    private readonly uint _number = (uint)Random.Shared.Next(1, int.MaxValue);
    private readonly byte[] _viewerNonce = LinkKeys.Random(Wire.NonceSize);
    private byte[]? _senderId, _senderPublicKey, _transcript, _linkSecret;
    private string? _pin;
    private PairingOffer? _offer;
    private CancellationTokenSource? _revealing;

    public PairingClient(Identity identity, string viewerName, IPEndPoint sender)
    {
        _identity = identity;
        _viewerName = viewerName;
        _sender = sender;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Connect(sender);
    }

    /// <summary>The sender as stored once pairing has succeeded.</summary>
    public Peer? Result { get; private set; }

    /// <summary>
    /// Asks to pair and waits for the sender's key. On return the sender is showing its PIN
    /// (the reveal is sent, and kept being sent until <see cref="ConfirmAsync"/> or disposal).
    /// Throws <see cref="PairingException"/> with the reason when the sender says no or never answers.
    /// </summary>
    public async Task<PairingOffer> StartAsync(CancellationToken cancel = default)
    {
        var request = new byte[128];
        int length = Pairing.WriteRequest(request, _number, _identity.Id,
                                          Pairing.Commitment(_identity.PublicKey, _viewerNonce), _viewerName);

        byte[] answer = await ExchangeAsync(request.AsMemory(0, length), Wire.PairKey, 10_000, cancel);
        if (!Pairing.TryReadKey(answer, out _senderId, out _senderPublicKey, out byte[] senderNonce, out string name, out string model) ||
            !Identity.IsValidPublicKey(_senderPublicKey) || !LinkKeys.Equal(Identity.IdOf(_senderPublicKey), _senderId))
            throw new PairingException("the sender's answer was not a key");

        _transcript = Pairing.Transcript(_identity.Id, _senderId, _identity.PublicKey, _senderPublicKey, _viewerNonce, senderNonce);
        _linkSecret = LinkKeys.LinkSecret(_identity, _senderPublicKey, _identity.PublicKey, _senderPublicKey);
        _pin = Pairing.Pin(_transcript);
        _offer = new PairingOffer(name, model, Identity.Hex(_senderId));

        // The reveal goes out now and again every half second until the person has typed the
        // PIN: the sender shows its PIN only once the reveal is in.
        var reveal = new byte[96];
        int revealLength = Pairing.WriteReveal(reveal, _number, _identity.PublicKey, _viewerNonce);
        _revealing = new CancellationTokenSource();
        var stop = _revealing.Token;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { _socket.Send(reveal.AsSpan(0, revealLength)); } catch (SocketException) { }
                try { await Task.Delay(500, stop); } catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
        return _offer;
    }

    /// <summary>
    /// Checks the PIN the person typed. False when it does not match - a typo, or someone in
    /// between; ask again. True when it matches and the sender has confirmed: <see cref="Result"/>
    /// is the sender to keep. Throws <see cref="PairingException"/> when the sender refuses or vanishes.
    /// </summary>
    public async Task<bool> ConfirmAsync(string typed, CancellationToken cancel = default)
    {
        if (_pin is null || _linkSecret is null || _transcript is null || _offer is null)
            throw new InvalidOperationException("StartAsync first");
        string digits = new(typed.Where(char.IsAsciiDigit).ToArray());
        if (!LinkKeys.Equal(Encoding.ASCII.GetBytes(digits), Encoding.ASCII.GetBytes(_pin))) return false;

        _revealing?.Cancel();
        var confirm = new byte[64];
        int length = Pairing.WriteProof(confirm, Wire.PairConfirm, _number, LinkKeys.Proof(_linkSecret, "pair confirm", _transcript));
        byte[] answer = await ExchangeAsync(confirm.AsMemory(0, length), Wire.PairDone, 10_000, cancel);
        if (!Pairing.TryReadProof(answer, out byte[] mac) || !LinkKeys.Equal(mac, LinkKeys.Proof(_linkSecret, "pair done", _transcript)))
            throw new PairingException(Pairing.Describe(Pairing.CancelReason.BadProof));

        Result = new Peer
        {
            Id = _offer.SenderId,
            Name = _offer.SenderName,
            Model = _offer.Model,
            PublicKey = Convert.ToBase64String(_senderPublicKey!),
            PairedMs = Clock.UnixMs,
            LastSeenMs = Clock.UnixMs,
            LastAddress = _sender.ToString(),
        };
        return true;
    }

    /// <summary>Tells the sender to stop showing the PIN.</summary>
    public void Cancel()
    {
        _revealing?.Cancel();
        var cancel = new byte[16];
        try { _socket.Send(cancel.AsSpan(0, Pairing.WriteCancel(cancel, _number, Pairing.CancelReason.Cancelled))); }
        catch (SocketException) { }
    }

    /// <summary>Sends <paramref name="message"/> twice a second until an answer of <paramref name="expected"/> type comes for this pairing.</summary>
    private async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> message, byte expected, int timeoutMs, CancellationToken cancel)
    {
        var buffer = new byte[Wire.DatagramMax];
        long deadline = Clock.NowNs + timeoutMs * 1_000_000L;
        while (Clock.NowNs < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            try { _socket.Send(message.Span); } catch (SocketException) { }

            long until = Math.Min(deadline, Clock.NowNs + 500_000_000L);
            while (Clock.NowNs < until)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                wait.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (until - Clock.NowNs) / 1_000_000)));
                int n;
                try
                {
                    n = await _socket.ReceiveAsync(buffer, SocketFlags.None, wait.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    // A port nobody listens on answers with an ICMP error: keep trying, it may be starting.
                    await Task.Delay(100, cancel);
                    continue;
                }

                var data = buffer.AsSpan(0, n);
                byte type = Wire.TypeOf(data);
                if (Wire.NumberOf(data) != _number) continue;
                if (type == Wire.PairCancel && n >= 9) throw new PairingException(Pairing.Describe((Pairing.CancelReason)data[8]));
                if (type == expected) return data.ToArray();
            }
        }
        throw new PairingException("the sender did not answer");
    }

    public void Dispose()
    {
        _revealing?.Cancel();
        _socket.Dispose();
    }
}

public sealed class PairingException(string message) : Exception(message);
