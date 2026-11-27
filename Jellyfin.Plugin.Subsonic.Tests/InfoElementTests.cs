using System.Collections.Generic;
using Jellyfin.Plugin.Subsonic.Response;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>
/// Spec: getAlbumInfo AND getAlbumInfo2 return &lt;albumInfo&gt;; getArtistInfo2 returns &lt;artistInfo2&gt;.
/// Music Assistant (py-opensonic) KeyErrors on "albumInfo2" (GH #6).
/// </summary>
public class InfoElementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlbumInfo_ElementIsAlwaysAlbumInfo(bool v2)
    {
        var xml = XmlBuilder.AlbumInfo("notes", "mbid", null, v2);
        Assert.Contains("<albumInfo ", xml);
        Assert.DoesNotContain("albumInfo2", xml);
    }

    [Fact]
    public void ArtistInfo2_KeepsArtistInfo2Element()
    {
        var xml = XmlBuilder.ArtistInfo(null, "mbid", null, null, new List<Dictionary<string, object?>>(), v2: true);
        Assert.Contains("<artistInfo2", xml);
    }
}
