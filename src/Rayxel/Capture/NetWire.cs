using System.Buffers.Binary;

namespace Rayxel.Capture;

/// <summary>
/// The datagrams exchanged with <c>rayxel-send</c>, the program on the Steam Deck that
/// captures and encodes its picture. This is the viewer's copy of
/// <c>src/rayxel-send/src/wire.h</c>, and the two have to be changed together.
/// <para>
/// One UDP socket each way, every datagram one message, all fields little-endian. The
/// viewer speaks first and keeps speaking: a HELLO twice a second says "still watching, at
/// this size and bitrate", and the sender stops capturing when they stop. Nothing is
/// acknowledged and nothing is resent — a frame with a packet missing is useless, and so
/// is every frame after it until the next keyframe, so the viewer asks for one.
/// </para>
/// </summary>
internal static class NetWire
{
    public const int DefaultPort = 45800;

    /// <summary>Bytes of picture per datagram; small enough that none is ever split in two.</summary>
    public const int PayloadMax = 1200;

    /// <summary>The most datagrams the sender will cut one frame into.</summary>
    public const int MaxPackets = 1024;

    public const int VideoHeaderSize = 40;

    public const int PadSize = 24, KeysSize = 44, MouseSize = 32;

    public const byte Hello = 1, Ping = 2, Pong = 3, Keyframe = 4, Video = 5, Bye = 6, Alive = 7, Pad = 8, Keys = 9, Mouse = 10;

    private const ushort Magic = 0x5852; // the bytes 'R' 'X'
    private const byte Version = 1;

    private static void WriteHeader(Span<byte> p, byte type)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p, Magic);
        p[2] = Version;
        p[3] = type;
    }

    /// <summary>The message type, or 0 when the datagram is not one of ours.</summary>
    public static byte TypeOf(ReadOnlySpan<byte> p) =>
        p.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(p) == Magic && p[2] == Version ? p[3] : (byte)0;

    public static int WriteHello(Span<byte> p, uint session, int width, int height, int fps, int bitrateKbps)
    {
        WriteHeader(p, Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(p[10..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], (ushort)fps);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(p[16..], (uint)bitrateKbps);
        return 20;
    }

    public static int WritePing(Span<byte> p, long viewerTimeNs)
    {
        WriteHeader(p, Ping);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], viewerTimeNs);
        return 16;
    }

    /// <summary>KEYFRAME and BYE carry nothing but the session they are about.</summary>
    public static int WriteSession(Span<byte> p, byte type, uint session)
    {
        WriteHeader(p, type);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        return 8;
    }

    /// <summary>
    /// A controller's whole state. The sender applies the newest it has seen and ignores
    /// any that arrive out of order, which is what the sequence number is for.
    /// </summary>
    public static int WritePad(Span<byte> p, uint session, uint sequence, in PadState state)
    {
        WriteHeader(p, Pad);
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

    /// <summary>
    /// The keyboard: every key that is down, one bit each, numbered by Linux key code.
    /// </summary>
    public static int WriteKeys(Span<byte> p, uint session, uint sequence, ReadOnlySpan<byte> down)
    {
        WriteHeader(p, Keys);
        BinaryPrimitives.WriteUInt32LittleEndian(p[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], sequence);
        down[..32].CopyTo(p[12..]);
        return KeysSize;
    }

    /// <summary>
    /// The mouse: its buttons, and running totals of its movement and of its wheels. Totals
    /// and not steps, so that a datagram lost on the way takes no movement with it.
    /// </summary>
    public static int WriteMouse(Span<byte> p, uint session, uint sequence, byte buttons, int x, int y, int wheel, int hwheel)
    {
        WriteHeader(p, Mouse);
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

    public static bool TryReadPong(ReadOnlySpan<byte> p, out long viewerTimeNs, out long senderTimeNs)
    {
        viewerTimeNs = senderTimeNs = 0;
        if (p.Length < 24) return false;
        viewerTimeNs = BinaryPrimitives.ReadInt64LittleEndian(p[8..]);
        senderTimeNs = BinaryPrimitives.ReadInt64LittleEndian(p[16..]);
        return true;
    }

    public static bool TryReadSession(ReadOnlySpan<byte> p, out uint session)
    {
        session = p.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(p[4..]) : 0;
        return p.Length >= 8;
    }

    /// <summary>What every datagram of a frame says about the frame it belongs to.</summary>
    /// <param name="CaptureTimeNs">Sender's clock: when gamescope handed the frame over.</param>
    /// <param name="EncodeUs">From then until the sender held the encoded frame.</param>
    public readonly record struct VideoHeader(
        uint Session, uint Frame, int Index, int Count, int FrameBytes,
        int Width, int Height, bool Keyframe, long CaptureTimeNs, int EncodeUs);

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
            Keyframe: (p[24] & 1) != 0,
            CaptureTimeNs: BinaryPrimitives.ReadInt64LittleEndian(p[28..]),
            EncodeUs: (int)BinaryPrimitives.ReadUInt32LittleEndian(p[36..]));
        return true;
    }
}
