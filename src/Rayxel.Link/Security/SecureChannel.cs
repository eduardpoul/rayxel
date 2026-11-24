using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rayxel.Link.Security;

/// <summary>
/// One direction's worth of sealing for a session, both ways: every message of version 1's
/// set - HELLO, PING, PONG, KEYFRAME, VIDEO, BYE, ALIVE, PAD, KEYS, MOUSE - travels under
/// AES-GCM with keys that exist for this session alone.
/// <para>
/// A sealed datagram is the message's own 8-byte header with the version set to 2, a 64-bit
/// counter, the rest of the message encrypted, and a 16-byte tag. The header and the counter
/// are authenticated but readable, so a receiver can tell whose session a datagram is for
/// before it decrypts anything. Opening one gives back the message in its plain layout, so
/// everything that reads messages reads them the same sealed or not.
/// </para>
/// <para>
/// The counter is the nonce: each side counts its own datagrams from 1 and never reuses a
/// number under one key. The receiver keeps the highest it has opened and a window of 64
/// below it, and refuses a number it has already seen or one that has fallen out of the
/// window - so nothing recorded can be played back, not even within the session.
/// </para>
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int CounterSize = 8, TagSize = 16, HeaderSize = 8;
    public const int Overhead = CounterSize + TagSize;

    private readonly AesGcm _seal, _open;
    private readonly object _sealGate = new(), _openGate = new();
    private long _sent;
    private ulong _highest, _window;

    /// <param name="sendKey">32 bytes: what this side seals with.</param>
    /// <param name="receiveKey">32 bytes: what the other side seals with.</param>
    public SecureChannel(ReadOnlySpan<byte> sendKey, ReadOnlySpan<byte> receiveKey)
    {
        _seal = new AesGcm(sendKey, TagSize);
        _open = new AesGcm(receiveKey, TagSize);
    }

    /// <summary>
    /// Seals a message written in its plain layout. <paramref name="output"/> needs
    /// <see cref="Overhead"/> bytes more than <paramref name="plain"/>. Returns its length.
    /// Safe to call from several threads.
    /// </summary>
    public int Seal(ReadOnlySpan<byte> plain, Span<byte> output)
    {
        if (plain.Length < HeaderSize) throw new ArgumentException("not a message", nameof(plain));
        int body = plain.Length - HeaderSize;
        plain[..HeaderSize].CopyTo(output);
        output[2] = Wire.Version2;

        ulong counter = (ulong)Interlocked.Increment(ref _sent);
        BinaryPrimitives.WriteUInt64LittleEndian(output[HeaderSize..], counter);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
        nonce[8..].Clear();

        const int start = HeaderSize + CounterSize;
        lock (_sealGate)
            _seal.Encrypt(nonce, plain[HeaderSize..], output.Slice(start, body), output.Slice(start + body, TagSize),
                          output[..start]);
        return start + body + TagSize;
    }

    /// <summary>
    /// Opens a sealed datagram into <paramref name="plain"/>, in the message's plain layout
    /// (version byte left at 2). False when it was not sealed by the other side of this
    /// session, has been tampered with, or has been seen before.
    /// </summary>
    public bool Open(ReadOnlySpan<byte> sealedMessage, Span<byte> plain, out int length)
    {
        length = 0;
        const int start = HeaderSize + CounterSize;
        if (sealedMessage.Length < start + TagSize) return false;
        int body = sealedMessage.Length - start - TagSize;
        if (plain.Length < HeaderSize + body) return false;

        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(sealedMessage[HeaderSize..]);
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, counter);
        nonce[8..].Clear();

        lock (_openGate)
        {
            if (!Fresh(counter)) return false;
            try
            {
                _open.Decrypt(nonce, sealedMessage.Slice(start, body), sealedMessage.Slice(start + body, TagSize),
                              plain.Slice(HeaderSize, body), sealedMessage[..start]);
            }
            catch (AuthenticationTagMismatchException)
            {
                return false;
            }
            Remember(counter);
        }

        sealedMessage[..HeaderSize].CopyTo(plain);
        length = HeaderSize + body;
        return true;
    }

    private bool Fresh(ulong counter)
    {
        if (counter == 0) return false;
        if (counter > _highest) return true;
        ulong behind = _highest - counter;
        return behind < 64 && (_window & (1UL << (int)behind)) == 0;
    }

    private void Remember(ulong counter)
    {
        if (counter > _highest)
        {
            ulong ahead = counter - _highest;
            _window = ahead >= 64 ? 1UL : (_window << (int)ahead) | 1UL;
            _highest = counter;
        }
        else
        {
            _window |= 1UL << (int)(_highest - counter);
        }
    }

    public void Dispose()
    {
        _seal.Dispose();
        _open.Dispose();
    }
}
