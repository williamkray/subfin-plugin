using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Subsonic.Auth;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class SubsonicAuthTests
{
    // ── DecodePassword ────────────────────────────────────────────────────────

    [Fact]
    public void DecodePassword_PlainText()
    {
        Assert.Equal("mypassword", SubsonicAuth.DecodePassword("mypassword"));
    }

    [Fact]
    public void DecodePassword_EncHex()
    {
        // "abc" in UTF-8 hex is 61 62 63
        Assert.Equal("abc", SubsonicAuth.DecodePassword("enc:616263"));
    }

    [Fact]
    public void DecodePassword_InvalidHex()
    {
        Assert.Null(SubsonicAuth.DecodePassword("enc:ZZZZ"));
    }

    [Fact]
    public void DecodePassword_Empty()
    {
        Assert.Null(SubsonicAuth.DecodePassword(""));
    }

    // ── ComputeToken ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeToken_KnownVector()
    {
        // Subsonic API test vector: password="sesam", salt="c19b2d"
        // token = md5("sesamc19b2d")
        var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("sesamc19b2d"))).ToLowerInvariant();
        Assert.Equal(expected, SubsonicAuth.ComputeToken("sesam", "c19b2d"));
    }
}
