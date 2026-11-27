using System;
using Jellyfin.Plugin.Subsonic.Controllers;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class StreamTests
{
    private static readonly Guid Item = Guid.Parse("134aa1532157fc943018227dc2333b1c");

    [Fact]
    public void TranscodeSessionId_StableForIdenticalRequests()
    {
        Assert.Equal(
            SubsonicController.TranscodeSessionId(Item, "subfin-1", "mp3", "mp3", 128, 60),
            SubsonicController.TranscodeSessionId(Item, "subfin-1", "mp3", "mp3", 128, 60));
    }

    [Theory]
    [InlineData("subfin-2", "mp3", "mp3", 128, 60)]   // other device
    [InlineData("subfin-1", "webm", "opus", 128, 60)] // other format
    [InlineData("subfin-1", "mp3", "mp3", 96, 60)]    // other bitrate
    [InlineData("subfin-1", "mp3", "mp3", 128, 0)]    // other offset (seek)
    public void TranscodeSessionId_ChangesWithOutputParameters(string device, string container, string codec, int kbps, int offset)
    {
        var baseline = SubsonicController.TranscodeSessionId(Item, "subfin-1", "mp3", "mp3", 128, 60);
        Assert.NotEqual(baseline, SubsonicController.TranscodeSessionId(Item, device, container, codec, kbps, offset));
    }

    [Fact]
    public void TranscodeSessionId_IsUrlSafeHex()
    {
        var id = SubsonicController.TranscodeSessionId(Item, null, "mp3", "mp3", 0, 0);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }
}
