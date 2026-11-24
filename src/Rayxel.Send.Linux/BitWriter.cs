namespace Rayxel.Send.Linux;

/// <summary>
/// A header's payload, built most significant bit first into memory the caller gives it -
/// the same writer for HEVC's NAL units and AV1's OBUs. It starts from zeros and only ever
/// sets bits, so the memory is cleared first.
/// <para>
/// Like the C writer it comes from, it stops writing rather than overrun its memory: a
/// header that would not fit comes out short, and the caller's size limit is what keeps
/// that from happening.
/// </para>
/// </summary>
internal ref struct BitWriter
{
    private readonly Span<byte> _data;

    public BitWriter(Span<byte> data)
    {
        _data = data;
        _data.Clear();
        Count = 0;
    }

    /// <summary>Bits written so far.</summary>
    public int Count { get; private set; }

    /// <summary>The whole bytes written so far: all of them once <see cref="PutTrailing"/> has been called.</summary>
    public readonly ReadOnlySpan<byte> Written => _data[..(Count / 8)];

    /// <summary>The low <paramref name="width"/> bits of <paramref name="value"/>, highest first.</summary>
    public void Put(uint value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            if (Count >= _data.Length * 8) return;
            if (((value >> i) & 1) != 0) _data[Count / 8] |= (byte)(0x80 >> (Count % 8));
            Count++;
        }
    }

    public void Put(bool flag, int width) => Put(flag ? 1u : 0u, width);

    /// <summary>Exp-Golomb, unsigned: as many zeros as the value plus one has bits after its first. HEVC's.</summary>
    public void PutUe(uint value)
    {
        uint code = value + 1;
        int width = 0;
        while ((code >> width) > 1) width++;
        Put(0, width);
        Put(code, width + 1);
    }

    /// <summary>Exp-Golomb, signed: positive values on the odd codes, the rest on the even ones. HEVC's.</summary>
    public void PutSe(int value) => PutUe(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * value));

    /// <summary>A one, then zeros up to the byte boundary: how every NAL payload and OBU here ends.</summary>
    public void PutTrailing()
    {
        Put(1, 1);
        while (Count % 8 != 0) Put(0, 1);
    }
}
