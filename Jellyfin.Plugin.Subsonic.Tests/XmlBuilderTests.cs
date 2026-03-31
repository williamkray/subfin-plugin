using System.Collections.Generic;
using System.Xml;
using Jellyfin.Plugin.Subsonic.Response;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

public class XmlBuilderTests
{
    private static XmlDocument Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    [Fact]
    public void Ping_ReturnsOkEnvelope()
    {
        var xml = XmlBuilder.Ping();
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        Assert.Equal("subsonic-response", root.LocalName);
        Assert.Equal("ok", root.GetAttribute("status"));
        Assert.Equal(SubsonicConstants.Version, root.GetAttribute("version"));
    }

    [Fact]
    public void ErrorEnvelope_HasFailedStatus()
    {
        var xml = XmlBuilder.ErrorEnvelope(40, "Wrong credentials");
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        Assert.Equal("failed", root.GetAttribute("status"));
        var error = root["error", "http://subsonic.org/restapi"]!;
        Assert.Equal("40", error.GetAttribute("code"));
        Assert.Equal("Wrong credentials", error.GetAttribute("message"));
    }

    [Fact]
    public void MusicFolders_UsesAttributes()
    {
        var folders = new List<(string Id, string Name)> { ("abc123", "Music"), ("def456", "Classical") };
        var xml = XmlBuilder.MusicFolders(folders);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var mf = root["musicFolders", "http://subsonic.org/restapi"]!;
        var first = (XmlElement)mf.ChildNodes[0]!;
        // Must be attributes, not child elements
        Assert.Equal("abc123", first.GetAttribute("id"));
        Assert.Equal("Music", first.GetAttribute("name"));
        // Must NOT have child element nodes for id/name
        Assert.Equal(0, first.ChildNodes.Count);
    }

    [Fact]
    public void Artists_IndexUsesAttributes()
    {
        var index = new List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)>
        {
            ("A", new List<(string, string, int)> { ("guid1", "ACDC", 10) }),
            ("B", new List<(string, string, int)> { ("guid2", "Beatles", 20) }),
        };
        var xml = XmlBuilder.Artists(index);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var artists = root["artists", "http://subsonic.org/restapi"]!;
        var firstIndex = (XmlElement)artists.ChildNodes[0]!;
        Assert.Equal("A", firstIndex.GetAttribute("name"));
        var firstArtist = (XmlElement)firstIndex.ChildNodes[0]!;
        Assert.Equal("guid1", firstArtist.GetAttribute("id"));
        Assert.Equal("ACDC", firstArtist.GetAttribute("name"));
        Assert.Equal("10", firstArtist.GetAttribute("albumCount"));
    }

    [Fact]
    public void Song_UsesAttributes_NotChildElements()
    {
        var song = new Dictionary<string, object?>
        {
            ["id"] = "abc", ["title"] = "Test Song", ["duration"] = 180, ["isDir"] = false
        };
        var xml = XmlBuilder.Song(song);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var s = root["song", "http://subsonic.org/restapi"]!;
        Assert.Equal("abc", s.GetAttribute("id"));
        Assert.Equal("Test Song", s.GetAttribute("title"));
        Assert.Equal("180", s.GetAttribute("duration"));
        // Must not have child elements with these names
        Assert.Null(s["id"]);
        Assert.Null(s["title"]);
    }

    [Fact]
    public void AlbumList_UsesAttributes()
    {
        var albums = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "al-guid1", ["name"] = "Abbey Road", ["artist"] = "The Beatles" }
        };
        var xml = XmlBuilder.AlbumList(albums);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var list = root["albumList", "http://subsonic.org/restapi"]!;
        var album = (XmlElement)list.ChildNodes[0]!;
        Assert.Equal("al-guid1", album.GetAttribute("id"));
        Assert.Equal("Abbey Road", album.GetAttribute("name"));
        // Must be attributes, not child elements
        Assert.Null(album["id"]);
        Assert.Null(album["name"]);
    }

    [Fact]
    public void Playlists_UsesAttributes()
    {
        var playlists = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "pl-guid1", ["name"] = "My Mix", ["songCount"] = 5 }
        };
        var xml = XmlBuilder.Playlists(playlists);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var pls = root["playlists", "http://subsonic.org/restapi"]!;
        var pl = (XmlElement)pls.ChildNodes[0]!;
        Assert.Equal("pl-guid1", pl.GetAttribute("id"));
        Assert.Equal("My Mix", pl.GetAttribute("name"));
        Assert.Null(pl["id"]);
        Assert.Null(pl["name"]);
    }

    [Fact]
    public void Song_HasMediaType()
    {
        var song = new Dictionary<string, object?>
        {
            ["id"] = "abc", ["title"] = "Track", ["mediaType"] = "song"
        };
        var xml = XmlBuilder.Song(song);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var s = root["song", "http://subsonic.org/restapi"]!;
        Assert.Equal("song", s.GetAttribute("mediaType"));
    }

    [Fact]
    public void SearchResult3_Structure()
    {
        var artists = new List<Dictionary<string, object?>> { new() { ["id"] = "ar-1", ["name"] = "Artist" } };
        var albums = new List<Dictionary<string, object?>> { new() { ["id"] = "al-1", ["name"] = "Album" } };
        var songs = new List<Dictionary<string, object?>> { new() { ["id"] = "s-1", ["title"] = "Song", ["mediaType"] = "song" } };
        var xml = XmlBuilder.SearchResult3(artists, albums, songs);
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var sr3 = root["searchResult3", "http://subsonic.org/restapi"]!;
        Assert.NotNull(sr3["artist", "http://subsonic.org/restapi"]);
        Assert.NotNull(sr3["album", "http://subsonic.org/restapi"]);
        Assert.NotNull(sr3["song", "http://subsonic.org/restapi"]);
    }

    [Fact]
    public void Share_AlwaysHasVisitCountAndExpires()
    {
        var share = new ShareXml(
            Id: "uid1",
            Url: "https://example.com/share/uid1?secret=abc",
            Description: null,
            Username: "alice",
            Created: "2026-01-01T00:00:00Z",
            Expires: "2027-01-01T00:00:00Z",
            VisitCount: 0,
            Songs: new List<Dictionary<string, object?>>());

        var xml = XmlBuilder.Shares(new List<ShareXml> { share });
        var doc = Parse(xml);
        var root = doc.DocumentElement!;
        var shares = root["shares", "http://subsonic.org/restapi"]!;
        var s = (XmlElement)shares.ChildNodes[0]!;

        // visitCount must be present and be an integer string
        var vc = s.GetAttribute("visitCount");
        Assert.Equal("0", vc);

        // expires must be present
        var exp = s.GetAttribute("expires");
        Assert.False(string.IsNullOrEmpty(exp), "expires must always be present");
    }
}
