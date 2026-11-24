using System.Security.Cryptography;

namespace Rayxel.Link.Security;

/// <summary>
/// How a private key is kept on disk. By default as it is, in a file only its owner may
/// read - which is how SteamOS and every Linux keeps such things. A Windows program sets
/// these to DPAPI (ProtectedData, current user), which ties the file to the Windows account.
/// </summary>
public static class KeyProtection
{
    public static Func<byte[], byte[]> Protect { get; set; } = bytes => bytes;
    public static Func<byte[], byte[]> Unprotect { get; set; } = bytes => bytes;
}

/// <summary>
/// This program's long-term key: an ECDH key pair on P-256, made the first time and kept.
/// Pairing teaches the other side its public half; from then on every session is proven
/// with it. Its id is the first 16 bytes of the SHA-256 of the public key, so an id cannot
/// be claimed without the key that goes with it.
/// </summary>
public sealed class Identity : IDisposable
{
    private readonly ECDiffieHellman _key;

    /// <summary>The public key, uncompressed: 0x04, then X and Y, 32 bytes each.</summary>
    public byte[] PublicKey { get; }

    public byte[] Id { get; }

    public string IdText => Hex(Id);

    private Identity(ECDiffieHellman key)
    {
        _key = key;
        PublicKey = Encode(key.ExportParameters(false));
        Id = IdOf(PublicKey);
    }

    /// <summary>The identity kept at <paramref name="path"/>, or a new one stored there.</summary>
    public static Identity LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var key = ECDiffieHellman.Create();
                key.ImportPkcs8PrivateKey(KeyProtection.Unprotect(File.ReadAllBytes(path)), out _);
                return new Identity(key);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                // A key that cannot be read is no key: every pairing made with it is lost
                // anyway. Keep the old file beside the new one rather than overwrite it.
                LinkLog.Warn($"identity: {path} could not be read ({ex.Message}); making a new one, pairings must be made again");
                try { File.Move(path, path + ".unreadable", true); } catch (IOException) { }
            }
        }

        var fresh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        byte[] stored = KeyProtection.Protect(fresh.ExportPkcs8PrivateKey());
        WritePrivate(path, stored);
        var identity = new Identity(fresh);
        LinkLog.Info($"identity: a new key made, id {identity.IdText}");
        return identity;
    }

    /// <summary>Writes a file only its owner can read, from the moment it exists.</summary>
    internal static void WritePrivate(string path, byte[] bytes)
    {
        string temporary = path + ".new";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(temporary, bytes);
        }
        else
        {
            using var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
        }
        File.Move(temporary, path, true);
    }

    /// <summary>The raw shared secret with the holder of <paramref name="peerPublicKey"/>: the same number on both sides.</summary>
    public byte[] Agree(byte[] peerPublicKey)
    {
        using var peer = FromPublic(peerPublicKey);
        return _key.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public static byte[] IdOf(ReadOnlySpan<byte> publicKey) => SHA256.HashData(publicKey)[..Wire.IdSize];

    public static bool IsValidPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != Wire.PublicKeySize || publicKey[0] != 0x04) return false;
        try
        {
            using var key = FromPublic(publicKey.ToArray());
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ECDiffieHellman FromPublic(byte[] publicKey)
    {
        if (publicKey.Length != Wire.PublicKeySize || publicKey[0] != 0x04)
            throw new CryptographicException("not an uncompressed P-256 public key");
        // Importing checks that the point is on the curve; one that is not is refused here.
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
    }

    private static byte[] Encode(ECParameters parameters)
    {
        var key = new byte[Wire.PublicKeySize];
        key[0] = 0x04;
        parameters.Q.X!.CopyTo(key, 1);
        parameters.Q.Y!.CopyTo(key, 33);
        return key;
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static byte[]? FromHex(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try { return Convert.FromHexString(text); }
        catch (FormatException) { return null; }
    }

    public void Dispose() => _key.Dispose();
}
