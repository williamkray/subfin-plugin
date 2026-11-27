using System;
using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Store;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class CryptoTests
{
    private static string TestSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Decrypt_KeyMatchesPreviousReleases()
    {
        // Known-answer key for this salt, as derived by the Rfc2898DeriveBytes constructor used up
        // to v10.11.5.7 (cross-checked with Python hashlib.pbkdf2_hmac). Blobs already stored in
        // users' databases must keep decrypting.
        var salt = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        var key = Convert.FromHexString("B80B69E27C2BA2C851E28B5ED1040F84F5D58952FA8E2001AD450BB38054213B");
        Crypto.SetSalt(salt);

        var iv = new byte[12];
        var plaintext = System.Text.Encoding.UTF8.GetBytes("stored-before-upgrade");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(iv, plaintext, ciphertext, tag);

        Assert.Equal("stored-before-upgrade", Crypto.Decrypt([.. iv, .. tag, .. ciphertext], salt));
    }

    [Fact]
    public void Decrypt_UsesKeyForTheGivenSalt()
    {
        var a = TestSalt();
        var b = TestSalt();
        Crypto.SetSalt(a);
        var blob = Crypto.Encrypt("x", b);   // must derive b's key, not reuse a's cached key
        Crypto.SetSalt(b);
        Assert.Equal("x", Crypto.Decrypt(blob, b));
    }

    [Fact]
    public void RoundTrip_ReturnsOriginalPlaintext()
    {
        var salt = TestSalt();
        Crypto.SetSalt(salt);

        var plaintext = "super-secret-app-password";
        var ciphertext = Crypto.Encrypt(plaintext, salt);
        var result = Crypto.Decrypt(ciphertext, salt);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachCall()
    {
        var salt = TestSalt();
        Crypto.SetSalt(salt);

        var plaintext = "same-password";
        var a = Crypto.Encrypt(plaintext, salt);
        var b = Crypto.Encrypt(plaintext, salt);

        // IV is random so ciphertext must differ
        Assert.False(a.AsSpan().SequenceEqual(b.AsSpan()), "Two encryptions of the same plaintext should not be identical (different IVs)");
    }

    [Fact]
    public void Decrypt_ThrowsOnTruncatedBlob()
    {
        var salt = TestSalt();
        Crypto.SetSalt(salt);
        Assert.Throws<ArgumentException>(() => Crypto.Decrypt(new byte[5], salt));
    }

    [Fact]
    public void RoundTrip_UnicodePayload()
    {
        var salt = TestSalt();
        Crypto.SetSalt(salt);

        var plaintext = "pässwörd-with-ünïcödé-🔑";
        var ciphertext = Crypto.Encrypt(plaintext, salt);
        var result = Crypto.Decrypt(ciphertext, salt);

        Assert.Equal(plaintext, result);
    }
}
