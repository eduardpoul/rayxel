using System.Buffers.Binary;
using System.Text;

namespace Rayxel.Link;

/// <summary>
/// A controller at one instant, in the units Windows reports and the wire carries: the
/// button bits are XInput's own, triggers run 0 to 255, sticks -32768 to 32767 with right
/// and up positive.
/// </summary>
public readonly record struct PadState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY);

/// <summary>
/// The datagrams a viewer and a sender exchange: one UDP socket each way, every datagram one
/// message, all fields little-endian. This is the only copy - the viewer, the sender and the
/// sender's user interfaces all use it. (The C sender in src/rayxel-send speaks version 1 of
/// it from its own wire.h.)
/// <para>
/// The viewer speaks first and keeps speaking: a HELLO twice a second says "still watching,
/// at this size, codec and bitrate", and the sender stops sending when they stop. Nothing is
/// acknowledged and nothing is resent - a frame with a packet missing is useless, and so is
/// every frame after it until the next keyframe, so the viewer asks for one.
/// </para>
/// <para>
/// Version 1 is that and nothing more: anyone on the network could ask for the picture, or
/// with input allowed, type on the sender. Version 2 adds who is talking. A viewer and a
/// sender are paired once (a PIN shown on the sender, typed into the viewer), each keeps the
/// other's public key, and every stream begins with CONNECT and WELCOME, after which every
/// message of version 1's - HELLO through MOUSE - travels sealed with AES-GCM under keys of
/// that session alone (see <see cref="Security.SecureChannel"/>). DISCOVER and ANNOUNCE find
/// senders, and the PAIR_ messages pair them; those travel in the clear.
/// </para>
/// </summary>
public static class Wire
{
    /// <summary>The codec byte's values.</summary>
    public enum Codec : byte
    {
        Hevc = 0,
        Av1 = 1,
        /// <summary>
        /// Intra-only: every frame stands alone. A frame is the codec's packets one after
        /// another, sent like any other frame and handed to the decoder whole.
        /// </summary>
        PyroWave = 2,
    }

    public const int CodecCount = 3;

    public static string CodecName(Codec codec) => codec switch
    {
        Codec.Av1 => "AV1",
        Codec.PyroWave => "PyroWave",
        Codec.Hevc => "HEVC",
        _ => $"codec {(byte)codec}"
    };

    public const int DefaultPort = 45800;

    /// <summary>Bytes of picture per datagram; small enough that none is ever split in two, sealed or not.</summary>
    public const int PayloadMax = 1200;

    /// <summary>The most datagrams one frame is cut into.</summary>
    public const int MaxPackets = 1024;

    public const int VideoHeaderSize = 40;
    public const int HelloSize = 20, PingSize = 16, PongSize = 24, SessionSize = 8;
    public const int PadSize = 24, KeysSize = 44, MouseSize = 32;

    /// <summary>The largest datagram either side ever sends: a sealed video packet.</summary>
    public const int DatagramMax = VideoHeaderSize + PayloadMax + Security.SecureChannel.Overhead;

    // Version 1's messages, sealed in version 2.
    public const byte Hello = 1, Ping = 2, Pong = 3, Keyframe = 4, Video = 5, Bye = 6, Alive = 7, Pad = 8, Keys = 9, Mouse = 10;

    // Version 2's own, in the clear.
    public const byte Discover = 32, Announce = 33;
    public const byte PairRequest = 34, PairKey = 35, PairReveal = 36, PairConfirm = 37, PairDone = 38, PairCancel = 39;
    public const byte Connect = 40, Welcome = 41, Refuse = 42;

    /// <summary>Whether a message of this type travels sealed in version 2.</summary>
    public static bool IsSealed(byte type) => type is >= Hello and <= Mouse;

    public const ushort Magic = 0x5852; // the bytes 'R' 'X'
    public const byte Version1 = 1, Version2 = 2;

    public const byte FlagKeyframe = 1;

    public const byte MouseLeft = 0x01, MouseRight = 0x02, MouseMiddle = 0x04, MouseBack = 0x08, MouseForward = 0x10;

    // The bits of PadState.Buttons: XInput's own, so the viewer passes its reading through untouched.
    public const ushort PadUp = 0x0001, PadDown = 0x0002, PadLeft = 0x0004, PadRight = 0x0008;
    public const ushort PadStart = 0x0010, PadBack = 0x0020, PadLeftThumb = 0x0040, PadRightThumb = 0x0080;
    public const ushort PadLeftShoulder = 0x0100, PadRightShoulder = 0x0200, PadGuide = 0x0400;
    public const ushort PadA = 0x1000, PadB = 0x2000, PadX = 0x4000, PadY = 0x8000;

    /// <summary>Why a sender turned a CONNECT down.</summary>
    public enum RefuseReason : byte
    {
        NotPaired = 1,  // this viewer is not one the sender knows
        Busy = 2,       // another viewer is watching
        Disabled = 3,   // the sender is switched off
        BadProof = 4,   // the keys did not match what was stored
    }

    // ----------------------------------------------------------------------------------
    // The header every message starts with. In version 2 every message carries a 32-bit
    // number at offset 4 too: the session for the stream's messages, the pairing for the
    // PAIR_ ones, the query for DISCOVER and ANNOUNCE.

    public static void WriteHeader(Span<byte> p, byte type, byte version = Version2)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p, Magic);
        p[2] = version;
        p[3] = type;
    }

    /// <summary>The message type, or 0 when the datagram is not one of ours or of an unknown version.</summary>
    public static byte TypeOf(ReadOnlySpan<byte> p) =>
        p.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(p) == Magic && p[2] is Version1 or Version2 ? p[3] : (byte)0;

    public static byte VersionOf(ReadOnlySpan<byte> p) => p.Length >= 4 ? p[2] : (byte)0;

    public static uint NumberOf(ReadOnlySpan<byte> p) => p.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(p[4..]) : 0;

    // ----------------------------------------------------------------------------------
    // Version 1's messages. Written with the version they are going out as: a sealed one
    // is first written in the clear and then sealed whole.

    public static int WriteHello(Span<byte> p, uint session, int width, int height, int fps, Codec codec, int bitrateKbps,
                                 byte version = Version2)
    {
        WriteHeader(p, Hello, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(p[10..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], (ushort)fps);
        p[14] = (byte)codec;
        p[15] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], (uint)bitrateKbps);
        return HelloSize;
    }

    public readonly record struct HelloMessage(uint Session, int Width, int Height, int Fps, Codec Codec, int BitrateKbps);

    public static bool TryReadHello(ReadOnlySpan<byte> p, out HelloMessage hello)
    {
        hello = default;
        if (p.Length < HelloSize) return false;
        hello = new HelloMessage(
            BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[12..]),
            (Codec)p[14],
            (int)BinaryPrimitives.ReadUInt32LittleEndian(p[16..]));
        return true;
    }

    /// <summary>The two clocks are unrelated, so the viewer measures the difference: it stamps a PING, the sender adds its own time.</summary>
    public static int WritePing(Span<byte> p, uint session, long viewerTimeNs, byte version = Version2)
    {
        WriteHeader(p, Ping, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], viewerTimeNs);
        return PingSize;
    }

    public static bool TryReadPing(ReadOnlySpan<byte> p, out long viewerTimeNs)
    {
        viewerTimeNs = p.Length >= PingSize ? BinaryPrimitives.ReadInt64LittleEndian(p[8..]) : 0;
        return p.Length >= PingSize;
    }

    public static int WritePong(Span<byte> p, uint session, long viewerTimeNs, long senderTimeNs, byte version = Version2)
    {
        WriteHeader(p, Pong, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], viewerTimeNs);
        BinaryPrimitives.WriteInt64LittleEndian(p[16..], senderTimeNs);
        return PongSize;
    }

    public static bool TryReadPong(ReadOnlySpan<byte> p, out long viewerTimeNs, out long senderTimeNs)
    {
        viewerTimeNs = senderTimeNs = 0;
        if (p.Length < PongSize) return false;
        viewerTimeNs = BinaryPrimitives.ReadInt64LittleEndian(p[8..]);
        senderTimeNs = BinaryPrimitives.ReadInt64LittleEndian(p[16..]);
        return true;
    }

    /// <summary>KEYFRAME, BYE and ALIVE carry nothing but the session they are about.</summary>
    public static int WriteSession(Span<byte> p, byte type, uint session, byte version = Version2)
    {
        WriteHeader(p, type, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        return SessionSize;
    }

    public static bool TryReadSession(ReadOnlySpan<byte> p, out uint session)
    {
        session = p.Length >= SessionSize ? BinaryPrimitives.ReadUInt32LittleEndian(p[4..]) : 0;
        return p.Length >= SessionSize;
    }

    /// <summary>
    /// A controller's whole state. The sender applies the newest it has seen and ignores any
    /// that arrive out of order, which is what the sequence number is for.
    /// </summary>
    public static int WritePad(Span<byte> p, uint session, uint sequence, in PadState state, byte version = Version2)
    {
        WriteHeader(p, Pad, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], state.Buttons);
        p[14] = state.LeftTrigger;
        p[15] = state.RightTrigger;
        BinaryPrimitives.WriteInt16LittleEndian(p[16..], state.LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(p[18..], state.LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(p[20..], state.RightX);
        BinaryPrimitives.WriteInt16LittleEndian(p[22..], state.RightY);
        return PadSize;
    }

    public static bool TryReadPad(ReadOnlySpan<byte> p, out uint session, out uint sequence, out PadState state)
    {
        session = sequence = 0;
        state = default;
        if (p.Length < PadSize) return false;
        session = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        state = new PadState(
            BinaryPrimitives.ReadUInt16LittleEndian(p[12..]), p[14], p[15],
            BinaryPrimitives.ReadInt16LittleEndian(p[16..]), BinaryPrimitives.ReadInt16LittleEndian(p[18..]),
            BinaryPrimitives.ReadInt16LittleEndian(p[20..]), BinaryPrimitives.ReadInt16LittleEndian(p[22..]));
        return true;
    }

    /// <summary>
    /// The keyboard: every key that is down, one bit each - byte n / 8, bit n % 8 is Linux key
    /// code n, which names the physical key and not the letter on it.
    /// </summary>
    public static int WriteKeys(Span<byte> p, uint session, uint sequence, ReadOnlySpan<byte> down, byte version = Version2)
    {
        WriteHeader(p, Keys, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], sequence);
        down[..32].CopyTo(p[12..]);
        return KeysSize;
    }

    public static bool TryReadKeys(ReadOnlySpan<byte> p, out uint session, out uint sequence, out ReadOnlySpan<byte> down)
    {
        session = sequence = 0;
        down = default;
        if (p.Length < KeysSize) return false;
        session = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        down = p.Slice(12, 32);
        return true;
    }

    /// <summary>
    /// The mouse: its buttons, and running totals of its movement and of its wheels (in 120ths
    /// of a notch). Totals and not steps, so that a datagram lost on the way takes no movement
    /// with it; they wrap round and that is expected.
    /// </summary>
    public static int WriteMouse(Span<byte> p, uint session, uint sequence, byte buttons, int x, int y, int wheel, int hwheel,
                                 byte version = Version2)
    {
        WriteHeader(p, Mouse, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], sequence);
        p[12] = buttons;
        p[13] = p[14] = p[15] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], x);
        BinaryPrimitives.WriteInt32LittleEndian(p[20..], y);
        BinaryPrimitives.WriteInt32LittleEndian(p[24..], wheel);
        BinaryPrimitives.WriteInt32LittleEndian(p[28..], hwheel);
        return MouseSize;
    }

    public readonly record struct MouseMessage(uint Session, uint Sequence, byte Buttons, int X, int Y, int Wheel, int HWheel);

    public static bool TryReadMouse(ReadOnlySpan<byte> p, out MouseMessage mouse)
    {
        mouse = default;
        if (p.Length < MouseSize) return false;
        mouse = new MouseMessage(
            BinaryPrimitives.ReadUInt32LittleEndian(p[4..]), BinaryPrimitives.ReadUInt32LittleEndian(p[8..]), p[12],
            BinaryPrimitives.ReadInt32LittleEndian(p[16..]), BinaryPrimitives.ReadInt32LittleEndian(p[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[24..]), BinaryPrimitives.ReadInt32LittleEndian(p[28..]));
        return true;
    }

    /// <summary>What every datagram of a frame says about the frame it belongs to.</summary>
    /// <param name="CaptureTimeNs">Sender's clock: when the frame reached the sender.</param>
    /// <param name="EncodeUs">From then until the sender held the encoded frame.</param>
    public readonly record struct VideoHeader(
        uint Session, uint Frame, int Index, int Count, int FrameBytes,
        int Width, int Height, bool Keyframe, Codec Codec, long CaptureTimeNs, int EncodeUs);

    public static void WriteVideoHeader(Span<byte> p, in VideoHeader h, byte version = Version2)
    {
        WriteHeader(p, Video, version);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], h.Session);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], h.Frame);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], (ushort)h.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], (ushort)h.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], (uint)h.FrameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(p[20..], (ushort)h.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(p[22..], (ushort)h.Height);
        p[24] = h.Keyframe ? FlagKeyframe : (byte)0;
        p[25] = (byte)h.Codec;
        p[26] = p[27] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(p[28..], h.CaptureTimeNs);
        BinaryPrimitives.WriteUInt32LittleEndian(p[36..], (uint)h.EncodeUs);
    }

    public static bool TryReadVideo(ReadOnlySpan<byte> p, out VideoHeader header)
    {
        header = default;
        if (p.Length < VideoHeaderSize) return false;
        header = new VideoHeader(
            Session: BinaryPrimitives.ReadUInt32LittleEndian(p[4..]),
            Frame: BinaryPrimitives.ReadUInt32LittleEndian(p[8..]),
            Index: BinaryPrimitives.ReadUInt16LittleEndian(p[12..]),
            Count: BinaryPrimitives.ReadUInt16LittleEndian(p[14..]),
            FrameBytes: (int)BinaryPrimitives.ReadUInt32LittleEndian(p[16..]),
            Width: BinaryPrimitives.ReadUInt16LittleEndian(p[20..]),
            Height: BinaryPrimitives.ReadUInt16LittleEndian(p[22..]),
            Keyframe: (p[24] & FlagKeyframe) != 0,
            Codec: (Codec)p[25],
            CaptureTimeNs: BinaryPrimitives.ReadInt64LittleEndian(p[28..]),
            EncodeUs: (int)BinaryPrimitives.ReadUInt32LittleEndian(p[36..]));
        return true;
    }

    // ----------------------------------------------------------------------------------
    // Version 2's own messages, in the clear. Names are UTF-8, at most NameMax bytes, with a
    // length byte in front.

    public const int NameMax = 64;
    public const int IdSize = 16, NonceSize = 16, PublicKeySize = 65, CommitSize = 32, MacSize = 32;

    internal static int WriteName(Span<byte> p, string name)
    {
        int length = Encoding.UTF8.GetBytes(Truncate(name), p[1..]);
        p[0] = (byte)length;
        return 1 + length;
    }

    internal static bool TryReadName(ReadOnlySpan<byte> p, out string name, out int used)
    {
        name = "";
        used = 0;
        if (p.Length < 1 || p.Length < 1 + p[0]) return false;
        name = Encoding.UTF8.GetString(p.Slice(1, p[0]));
        used = 1 + p[0];
        return true;
    }

    /// <summary>A name cut to NameMax bytes of UTF-8 without splitting a character.</summary>
    public static string Truncate(string name)
    {
        name = name.Trim();
        while (Encoding.UTF8.GetByteCount(name) > NameMax) name = name[..^1];
        return name;
    }

    /// <summary>DISCOVER: "who is there?", broadcast by a viewer. A sender answers when it may be found, or when it knows this viewer.</summary>
    public static int WriteDiscover(Span<byte> p, uint query, ReadOnlySpan<byte> viewerId)
    {
        WriteHeader(p, Discover);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], query);
        viewerId[..IdSize].CopyTo(p[8..]);
        return 8 + IdSize;
    }

    public static bool TryReadDiscover(ReadOnlySpan<byte> p, out uint query, out ReadOnlySpan<byte> viewerId)
    {
        query = NumberOf(p);
        viewerId = p.Length >= 8 + IdSize ? p.Slice(8, IdSize) : default;
        return p.Length >= 8 + IdSize;
    }

    [Flags]
    public enum AnnounceFlags : byte
    {
        None = 0,
        Discoverable = 1, // pairing is open: the sender is waiting to be found
        KnowsYou = 2,     // the viewer that asked is one it is paired with
        Streaming = 4,    // somebody is watching now
        Enabled = 8,      // switched on
    }

    /// <summary>What a sender says of itself in ANNOUNCE.</summary>
    public sealed record AnnounceMessage(
        uint Query, byte[] SenderId, AnnounceFlags Flags, byte CodecMask, int Port, string Name, string Model);

    public static int WriteAnnounce(Span<byte> p, AnnounceMessage a)
    {
        WriteHeader(p, Announce);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], a.Query);
        a.SenderId.AsSpan(0, IdSize).CopyTo(p[8..]);
        int at = 8 + IdSize;
        p[at++] = (byte)a.Flags;
        p[at++] = a.CodecMask;
        BinaryPrimitives.WriteUInt16LittleEndian(p[at..], (ushort)a.Port);
        at += 2;
        at += WriteName(p[at..], a.Name);
        at += WriteName(p[at..], a.Model);
        return at;
    }

    public static bool TryReadAnnounce(ReadOnlySpan<byte> p, out AnnounceMessage announce)
    {
        announce = null!;
        int at = 8 + IdSize;
        if (p.Length < at + 4) return false;
        var flags = (AnnounceFlags)p[at];
        byte codecs = p[at + 1];
        int port = BinaryPrimitives.ReadUInt16LittleEndian(p[(at + 2)..]);
        at += 4;
        if (!TryReadName(p[at..], out string name, out int used)) return false;
        at += used;
        if (!TryReadName(p[at..], out string model, out _)) return false;
        announce = new AnnounceMessage(NumberOf(p), p.Slice(8, IdSize).ToArray(), flags, codecs, port, name, model);
        return true;
    }

    /// <summary>CONNECT: a paired viewer asks for a session. Its id, and a fresh nonce for the session's keys.</summary>
    public static int WriteConnect(Span<byte> p, uint session, ReadOnlySpan<byte> viewerId, ReadOnlySpan<byte> viewerNonce)
    {
        WriteHeader(p, Connect);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        viewerId[..IdSize].CopyTo(p[8..]);
        viewerNonce[..NonceSize].CopyTo(p[(8 + IdSize)..]);
        return 8 + IdSize + NonceSize;
    }

    public static bool TryReadConnect(ReadOnlySpan<byte> p, out uint session, out ReadOnlySpan<byte> viewerId,
                                      out ReadOnlySpan<byte> viewerNonce)
    {
        session = NumberOf(p);
        viewerId = viewerNonce = default;
        if (p.Length < 8 + IdSize + NonceSize) return false;
        viewerId = p.Slice(8, IdSize);
        viewerNonce = p.Slice(8 + IdSize, NonceSize);
        return true;
    }

    /// <summary>WELCOME: the sender's nonce, and proof that it holds the key the viewer paired with.</summary>
    public static int WriteWelcome(Span<byte> p, uint session, ReadOnlySpan<byte> senderId, ReadOnlySpan<byte> senderNonce,
                                   ReadOnlySpan<byte> proof)
    {
        WriteHeader(p, Welcome);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        senderId[..IdSize].CopyTo(p[8..]);
        senderNonce[..NonceSize].CopyTo(p[(8 + IdSize)..]);
        proof[..MacSize].CopyTo(p[(8 + IdSize + NonceSize)..]);
        return 8 + IdSize + NonceSize + MacSize;
    }

    public static bool TryReadWelcome(ReadOnlySpan<byte> p, out uint session, out ReadOnlySpan<byte> senderId,
                                      out ReadOnlySpan<byte> senderNonce, out ReadOnlySpan<byte> proof)
    {
        session = NumberOf(p);
        senderId = senderNonce = proof = default;
        if (p.Length < 8 + IdSize + NonceSize + MacSize) return false;
        senderId = p.Slice(8, IdSize);
        senderNonce = p.Slice(8 + IdSize, NonceSize);
        proof = p.Slice(8 + IdSize + NonceSize, MacSize);
        return true;
    }

    public static int WriteRefuse(Span<byte> p, uint session, RefuseReason reason)
    {
        WriteHeader(p, Refuse);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        p[8] = (byte)reason;
        return 9;
    }

    public static bool TryReadRefuse(ReadOnlySpan<byte> p, out uint session, out RefuseReason reason)
    {
        session = NumberOf(p);
        reason = p.Length >= 9 ? (RefuseReason)p[8] : 0;
        return p.Length >= 9;
    }
}
