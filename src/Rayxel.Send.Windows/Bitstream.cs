using System.Buffers.Binary;
using Rayxel.Link;

namespace Rayxel.Send.Windows;

/// <summary>
/// An encoded frame being put together, in memory that never moves, so that an
/// <see cref="Rayxel.Link.Media.EncodedFrame"/> can point into it until the next one.
/// </summary>
internal sealed unsafe class FrameBuffer(int capacity)
{
    private byte[] _bytes = GC.AllocateUninitializedArray<byte>(Math.Max(capacity, 4096), pinned: true);

    public int Length { get; private set; }

    /// <summary>The start of the bytes. Pinned from birth, so the address stays good after the fixed block.</summary>
    public byte* Pointer
    {
        get
        {
            fixed (byte* bytes = _bytes) return bytes;
        }
    }

    public ReadOnlySpan<byte> Span => _bytes.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void Append(byte value)
    {
        Ensure(Length + 1);
        _bytes[Length++] = value;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        Ensure(Length + bytes.Length);
        bytes.CopyTo(_bytes.AsSpan(Length));
        Length += bytes.Length;
    }

    /// <summary>An unsigned number as AV1 writes sizes: seven bits a byte, least significant first.</summary>
    public void AppendLeb128(ulong value)
    {
        do
        {
            byte low = (byte)(value & 0x7F);
            value >>= 7;
            Append(value != 0 ? (byte)(low | 0x80) : low);
        } while (value != 0);
    }

    private void Ensure(int needed)
    {
        if (needed <= _bytes.Length) return;
        var larger = GC.AllocateUninitializedArray<byte>(Math.Max(needed, _bytes.Length * 2), pinned: true);
        _bytes.AsSpan(0, Length).CopyTo(larger);
        _bytes = larger;
    }
}

/// <summary>
/// HEVC access units as a Media Foundation encoder hands them over, made into what Rayxel's
/// viewer decodes: Annex B, one access unit a frame, with the VPS, SPS and PPS in front of
/// every keyframe. A viewer that joins late or has lost a packet starts again at a keyframe,
/// and can only start there if the keyframe brings its parameter sets. Encoders differ in
/// whether they repeat them; some put them only in the output type's
/// MF_MT_MPEG_SEQUENCE_HEADER. So the last ones seen, inline or there, are kept, and put in
/// front of any keyframe that comes without.
/// </summary>
internal sealed class HevcFrames
{
    private const int Vps = 32, Sps = 33, Pps = 34, Aud = 35, Filler = 38;
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    private byte[] _parameterSets = [];
    private bool _saidMissing, _saidLengths;
    private readonly List<Range> _units = [];

    public bool HasParameterSets => _parameterSets.Length > 0;

    /// <summary>MF_MT_MPEG_SEQUENCE_HEADER: Annex B as a rule, an hvcC record now and then.</summary>
    public void TakeSequenceHeader(ReadOnlySpan<byte> blob)
    {
        var sets = new FrameBuffer(blob.Length + 64);
        if (IsAnnexB(blob))
        {
            Split(blob, _units);
            foreach (var unit in _units)
            {
                var nal = blob[unit];
                if (nal.Length > 0 && Type(nal) is Vps or Sps or Pps)
                {
                    sets.Append(StartCode);
                    sets.Append(nal);
                }
            }
        }
        else if (blob.Length > 23 && blob[0] == 1)
        {
            // hvcC: 22 bytes of profile and such, the number of arrays, then each array's
            // NAL unit type, count, and units with 16-bit lengths.
            int at = 22, arrays = blob[at++];
            for (int a = 0; a < arrays && at + 3 <= blob.Length; a++)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(blob[(at + 1)..]);
                at += 3;
                for (int n = 0; n < count && at + 2 <= blob.Length; n++)
                {
                    int length = BinaryPrimitives.ReadUInt16BigEndian(blob[at..]);
                    at += 2;
                    if (at + length > blob.Length) break;
                    var nal = blob.Slice(at, length);
                    at += length;
                    if (nal.Length > 0 && Type(nal) is Vps or Sps or Pps)
                    {
                        sets.Append(StartCode);
                        sets.Append(nal);
                    }
                }
            }
        }
        if (sets.Length > 0) _parameterSets = sets.Span.ToArray();
    }

    /// <summary>
    /// Writes one access unit into <paramref name="output"/>, every NAL unit behind a
    /// four-byte start code. Whether it is a keyframe: the encoder's word for it, or an IRAP
    /// picture in it.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> sample, bool cleanPoint, FrameBuffer output)
    {
        output.Clear();
        if (!IsAnnexB(sample) && !SplitLengthPrefixed(sample, _units))
        {
            // Neither form: passed on as it is, for the decoder to make of what it can.
            output.Append(sample);
            return cleanPoint;
        }
        if (IsAnnexB(sample)) Split(sample, _units);
        else if (!_saidLengths)
        {
            _saidLengths = true;
            LinkLog.Info("encoder: the encoder gives NAL units with lengths in front; they are given start codes here");
        }

        bool vps = false, sps = false, pps = false, irap = false;
        foreach (var unit in _units)
        {
            var nal = sample[unit];
            if (nal.Length == 0) continue;
            int type = Type(nal);
            vps |= type == Vps;
            sps |= type == Sps;
            pps |= type == Pps;
            irap |= type is >= 16 and <= 23;
        }
        bool keyframe = cleanPoint || irap;

        if (vps && sps && pps)
        {
            var sets = new FrameBuffer(256);
            foreach (var unit in _units)
            {
                var nal = sample[unit];
                if (nal.Length > 0 && Type(nal) is Vps or Sps or Pps)
                {
                    sets.Append(StartCode);
                    sets.Append(nal);
                }
            }
            _parameterSets = sets.Span.ToArray();
        }

        int first = 0;
        if (_units.Count > 0 && sample[_units[0]].Length > 0 && Type(sample[_units[0]]) == Aud)
        {
            // An access unit delimiter has to stay first.
            output.Append(StartCode);
            output.Append(sample[_units[0]]);
            first = 1;
        }
        if (keyframe && !(vps && sps && pps))
        {
            if (_parameterSets.Length > 0) output.Append(_parameterSets);
            else if (!_saidMissing)
            {
                _saidMissing = true;
                LinkLog.Warn("encoder: a keyframe came without parameter sets and none are known; a viewer cannot start on it");
            }
        }
        for (int i = first; i < _units.Count; i++)
        {
            // Filler data is how CBR makes up a frame's bits for the decoder model's sake;
            // the viewer has no use for it, and on the wire it is only more to lose.
            var nal = sample[_units[i]];
            if (nal.Length == 0 || Type(nal) == Filler) continue;
            output.Append(StartCode);
            output.Append(nal);
        }
        return keyframe;
    }

    private static int Type(ReadOnlySpan<byte> nal) => (nal[0] >> 1) & 0x3F;

    private static bool IsAnnexB(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    /// <summary>The NAL units of an Annex B stream, start codes and the zeros before them left out.</summary>
    private static void Split(ReadOnlySpan<byte> data, List<Range> units)
    {
        units.Clear();
        int start = -1;
        for (int i = 0; i + 2 < data.Length;)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) units.Add(new Range(start, TrimZeros(data, start, i)));
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0) units.Add(new Range(start, TrimZeros(data, start, data.Length)));
    }

    /// <summary>
    /// A NAL unit never ends in a zero byte, so zeros before a start code belong to the start
    /// code (its four-byte form) or are padding between units.
    /// </summary>
    private static int TrimZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0) end--;
        return end;
    }

    /// <summary>NAL units behind four-byte big-endian lengths, as in MP4. False unless the lengths account for every byte.</summary>
    private static bool SplitLengthPrefixed(ReadOnlySpan<byte> data, List<Range> units)
    {
        units.Clear();
        int at = 0;
        while (at + 4 <= data.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            at += 4;
            if (length <= 0 || at + length > data.Length) return false;
            units.Add(new Range(at, at + length));
            at += length;
        }
        return at == data.Length && units.Count > 0;
    }
}

/// <summary>
/// AV1 temporal units as a Media Foundation encoder hands them over, made into what Rayxel's
/// viewer decodes: OBUs in the low-overhead form (each with its size), a temporal delimiter
/// first, and the sequence header in front of every keyframe, for the same reason the HEVC
/// parameter sets go there. The sequence header is kept from the last keyframe that had one,
/// or from MF_MT_MPEG_SEQUENCE_HEADER, which holds either bare OBUs or an av1C record.
/// </summary>
internal sealed class Av1Frames
{
    private const int SequenceHeader = 1, TemporalDelimiter = 2, FrameHeader = 3, Frame = 6, Padding = 15;

    private byte[] _sequenceHeader = [];
    private bool _reducedStillPicture;
    private bool _saidMissing, _saidUnreadable;

    public bool HasSequenceHeader => _sequenceHeader.Length > 0;

    public void TakeSequenceHeader(ReadOnlySpan<byte> blob)
    {
        // An av1C record starts with its marker bit set, which an OBU header never has.
        if (blob.Length >= 4 && (blob[0] & 0x80) != 0) blob = blob[4..];
        int at = 0;
        while (Next(blob, ref at, out var obu))
        {
            if (obu.Type != SequenceHeader) continue;
            Keep(obu);
            return;
        }
    }

    /// <summary>Writes one temporal unit into <paramref name="output"/>. Whether it is a keyframe.</summary>
    public bool Write(ReadOnlySpan<byte> sample, bool cleanPoint, FrameBuffer output)
    {
        output.Clear();
        output.Append((TemporalDelimiter << 3) | 0x02);
        output.Append(0);

        bool sequence = false, frameKey = false, sawFrame = false;
        int at = 0;
        while (Next(sample, ref at, out var obu))
        {
            if (obu.Type == SequenceHeader)
            {
                sequence = true;
                Keep(obu);
            }
            else if (obu.Type is FrameHeader or Frame && !sawFrame)
            {
                sawFrame = true;
                frameKey = IsKeyFrame(obu.Payload);
            }
        }
        if (at != sample.Length)
        {
            // Not OBUs as this reads them: passed on whole behind the delimiter.
            if (!_saidUnreadable)
            {
                _saidUnreadable = true;
                LinkLog.Warn("encoder: the AV1 encoder's output is not in the low-overhead OBU form; it is passed on as it is");
            }
            output.Append(sample);
            return cleanPoint;
        }

        bool keyframe = cleanPoint || frameKey;
        if (keyframe && !sequence)
        {
            if (_sequenceHeader.Length > 0) output.Append(_sequenceHeader);
            else if (!_saidMissing)
            {
                _saidMissing = true;
                LinkLog.Warn("encoder: a keyframe came without a sequence header and none is known; a viewer cannot start on it");
            }
        }

        at = 0;
        while (Next(sample, ref at, out var obu))
        {
            // The delimiter is written above, once; padding is for rate control's books, not
            // for the viewer, and a datagram saved is a datagram that cannot be lost.
            if (obu.Type is TemporalDelimiter or Padding) continue;
            Append(output, obu);
        }
        return keyframe;
    }

    private void Keep(in Obu obu)
    {
        var whole = new FrameBuffer(obu.Payload.Length + 16);
        Append(whole, obu);
        _sequenceHeader = whole.Span.ToArray();
        // seq_profile (3 bits), still_picture (1), reduced_still_picture_header (1).
        _reducedStillPicture = obu.Payload.Length > 0 && (obu.Payload[0] & 0x08) != 0;
    }

    /// <summary>
    /// From the first bits of a frame header: show_existing_frame, then frame_type, of which
    /// 0 is KEY_FRAME. A stream of still pictures has neither and every frame is a key frame.
    /// </summary>
    private bool IsKeyFrame(ReadOnlySpan<byte> header)
    {
        if (_reducedStillPicture) return true;
        if (header.Length == 0 || (header[0] & 0x80) != 0) return false;
        return ((header[0] >> 5) & 3) == 0;
    }

    private static void Append(FrameBuffer output, in Obu obu)
    {
        output.Append((byte)(obu.Header[0] | 0x02));
        if (obu.Header.Length > 1) output.Append(obu.Header[1]);
        output.AppendLeb128((ulong)obu.Payload.Length);
        output.Append(obu.Payload);
    }

    private readonly ref struct Obu(int type, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        public readonly int Type = type;
        public readonly ReadOnlySpan<byte> Header = header;
        public readonly ReadOnlySpan<byte> Payload = payload;
    }

    /// <summary>
    /// The next OBU from <paramref name="at"/>, which it moves past it. One without a size
    /// takes the rest of the data, which only the last may do. False at the end, and at
    /// anything that is not an OBU, leaving <paramref name="at"/> short of the end.
    /// </summary>
    private static bool Next(ReadOnlySpan<byte> data, ref int at, out Obu obu)
    {
        obu = default;
        if (at >= data.Length) return false;
        byte header = data[at];
        if ((header & 0x80) != 0) return false;

        int type = (header >> 3) & 0x0F;
        int headerLength = (header & 0x04) != 0 ? 2 : 1;
        if (at + headerLength > data.Length) return false;

        int payload = at + headerLength;
        long size;
        if ((header & 0x02) != 0)
        {
            if (!ReadLeb128(data, ref payload, out ulong value) || value > int.MaxValue) return false;
            size = (long)value;
        }
        else
        {
            size = data.Length - payload;
        }
        if (payload + size > data.Length) return false;

        obu = new Obu(type, data.Slice(at, headerLength), data.Slice(payload, (int)size));
        at = payload + (int)size;
        return true;
    }

    private static bool ReadLeb128(ReadOnlySpan<byte> data, ref int at, out ulong value)
    {
        value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (at >= data.Length) return false;
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }
}
