using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Rayxel.Link.Security;

/// <summary>
/// The keys that follow from a pairing. Two paired programs share one secret for good - the
/// link secret, from their long-term keys - and every session draws its own pair of keys from
/// it and two fresh nonces, one from each side.
/// </summary>
public static class LinkKeys
{
    /// <summary>
    /// The secret a viewer and a sender share once paired: ECDH between their long-term
    /// keys, through HKDF salted with both public keys, viewer's first. Either side gets the
    /// same 32 bytes from its own private key and the other's public one.
    /// </summary>
    public static byte[] LinkSecret(Identity mine, byte[] peerPublicKey, byte[] viewerPublicKey, byte[] senderPublicKey)
    {
        byte[] shared = mine.Agree(peerPublicKey);
        byte[] salt = SHA256.HashData([.. viewerPublicKey, .. senderPublicKey]);
        byte[] secret = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.ASCII.GetBytes("rayxel link v2"));
        CryptographicOperations.ZeroMemory(shared);
        return secret;
    }

    /// <summary>One session's keys: the viewer's for sealing what it sends, and the sender's.</summary>
    public static (byte[] ViewerToSender, byte[] SenderToViewer) SessionKeys(
        byte[] linkSecret, ReadOnlySpan<byte> viewerNonce, ReadOnlySpan<byte> senderNonce, uint session)
    {
        byte[] salt = [.. viewerNonce, .. senderNonce];
        var info = new byte[22];
        Encoding.ASCII.GetBytes("rayxel session", info);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(18), session);
        byte[] keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, linkSecret, 64, salt, info);
        return (keys[..32], keys[32..]);
    }

    /// <summary>HMAC-SHA256 under <paramref name="key"/> of a label and whatever follows it.</summary>
    public static byte[] Proof(byte[] key, string label, params byte[][] parts)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.ASCII.GetBytes(label));
        foreach (byte[] part in parts) hmac.AppendData(part);
        return hmac.GetHashAndReset();
    }

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] Random(int count) => RandomNumberGenerator.GetBytes(count);

    public static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);
}
