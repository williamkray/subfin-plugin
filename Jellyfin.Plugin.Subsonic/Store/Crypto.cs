using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Subsonic.Store;

/// <summary>
/// AES-256-GCM encrypt/decrypt for DB storage.
/// Wire format: IV (12) | AuthTag (16) | Ciphertext.
/// Key derived from plugin salt via Rfc2898 (PBKDF2-SHA256, 100k iterations).
/// </summary>
public static class Crypto
{
    private const int IvLen = 12;
    private const int TagLen = 16;
    private const int KeyLen = 32;
    private const string KdfSalt = "subfin-db-encryption-v1";

    // Cache keyed by salt: re-deriving costs 100k PBKDF2 iterations per call.
    // Immutable reference type so concurrent readers never see a torn (salt, key) pair.
    private sealed record KeyEntry(string Salt, byte[] Key);
    private static volatile KeyEntry? _cachedKey;

    public static void SetSalt(string base64Salt)
    {
        _cachedKey = null; // reset so next call re-derives
        _GetKey(base64Salt); // pre-warm
    }

    private static byte[] _GetKey(string base64Salt)
    {
        var cached = _cachedKey;
        if (cached != null && cached.Salt == base64Salt) return cached.Key;
        // Same derivation as the obsolete Rfc2898DeriveBytes constructor used before (SYSLIB0060
        // on .NET 10); CryptoTests.Decrypt_KeyMatchesPreviousReleases pins the output.
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Convert.FromBase64String(base64Salt),
            Encoding.UTF8.GetBytes(KdfSalt),
            100_000, HashAlgorithmName.SHA256, KeyLen);
        _cachedKey = new KeyEntry(base64Salt, key);
        return key;
    }

    public static byte[] Encrypt(string plaintext, string salt)
    {
        var key = _GetKey(salt);
        var iv = RandomNumberGenerator.GetBytes(IvLen);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagLen];

        using var aes = new AesGcm(key, TagLen);
        aes.Encrypt(iv, plaintextBytes, ciphertext, tag);

        var result = new byte[IvLen + TagLen + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IvLen);
        Buffer.BlockCopy(tag, 0, result, IvLen, TagLen);
        Buffer.BlockCopy(ciphertext, 0, result, IvLen + TagLen, ciphertext.Length);
        return result;
    }

    public static string Decrypt(byte[] blob, string salt)
    {
        if (blob.Length < IvLen + TagLen)
            throw new ArgumentException("Invalid encrypted blob");

        var key = _GetKey(salt);
        var iv = blob[..IvLen];
        var tag = blob[IvLen..(IvLen + TagLen)];
        var ciphertext = blob[(IvLen + TagLen)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagLen);
        aes.Decrypt(iv, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
