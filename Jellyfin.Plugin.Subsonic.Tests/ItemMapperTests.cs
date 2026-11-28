using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Subsonic.Mappers;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class ItemMapperTests
{
    [Theory]
    [InlineData("ar-abc123", "abc123")]
    [InlineData("al-def456", "def456")]
    [InlineData("pl-xyz789", "xyz789")]
    [InlineData("rawguid", "rawguid")]
    [InlineData("AR-UPPERCASE", "UPPERCASE")]
    public void StripPrefix_RemovesPrefixes(string input, string expected)
    {
        Assert.Equal(expected, ItemMapper.StripPrefix(input));
    }

    [Theory]
    [InlineData("ABBA", "A")]
    [InlineData("Beatles", "B")]
    [InlineData("123band", "1")]
    [InlineData("", "#")]
    [InlineData(null, "#")]
    [InlineData("élan", "#")]
    public void IndexLetter_ReturnsCorrectLetter(string? name, string expected)
    {
        Assert.Equal(expected, ItemMapper.IndexLetter(name));
    }

    [Theory]
    [InlineData(10_000_000L, 1)]
    [InlineData(100_000_000L, 10)]
    [InlineData(0L, 0)]
    [InlineData(null, 0)]
    public void TicksToSeconds_Converts(long? ticks, int expected)
    {
        Assert.Equal(expected, ItemMapper.TicksToSeconds(ticks));
    }

    [Theory]
    // Jellyfin's Container for MP4-family files is ffprobe's whole format list (GH #8)
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "/music/a.m4a", "m4a")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "/music/a.MP4", "mp4")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "/music/a.m4b", "m4b")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", null, "m4a")]
    [InlineData("mp3", "/music/a.mp3", "mp3")]
    [InlineData("ogg", "/music/a.opus", "opus")]
    [InlineData("flac", "/music/v1.0/noext", "flac")]
    [InlineData("MP3", null, "mp3")]
    [InlineData("", null, "mp3")]
    [InlineData(null, null, "mp3")]
    public void AudioSuffix_IsOneFileType(string? container, string? path, string expected)
    {
        Assert.Equal(expected, ItemMapper.AudioSuffix(container, path));
    }

    [Theory]
    [InlineData("m4a", "audio/mp4")]
    [InlineData("mp4", "audio/mp4")]
    [InlineData("aac", "audio/aac")]
    [InlineData("mp3", "audio/mpeg")]
    [InlineData("FLAC", "audio/flac")]
    [InlineData("xyz", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void AudioMimeType_MapsSuffix(string? suffix, string expected)
    {
        Assert.Equal(expected, ItemMapper.AudioMimeType(suffix));
    }

    [Fact]
    public void ToArtistsIndex_GroupsByLetter()
    {
        var artists = new List<(string Id, string Name, int AlbumCount)>
        {
            ("id1", "ABBA", 5),
            ("id2", "Beatles", 3),
            ("id3", "AC/DC", 8),
        };
        var result = ItemMapper.ToArtistsIndex(artists);
        Assert.True(result.ContainsKey("ignoredArticles"));
        var index = result["index"] as System.Collections.IList;
        Assert.NotNull(index);
        // Should have at least A and B groups
        Assert.True(index!.Count >= 2);
    }
}
