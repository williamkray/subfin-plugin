using System;
using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Store;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class CryptoTests
{
    private static string TestSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
