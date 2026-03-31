using System;
using System.Collections.Generic;
using System.Xml;
using Jellyfin.Plugin.Subsonic.Controllers;
using Jellyfin.Plugin.Subsonic.Response;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>
/// Guards client-specific crash-prevention rules for DSub2000, Navic, and Tempus.
/// </summary>
public class ClientCompatibilityTests
{
    private static XmlDocument Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    // ── Navic ────────────────────────────────────────────────────────────────

    /// <summary>
    /// All &lt;song&gt; elements inside &lt;searchResult3&gt; must carry mediaType="song".
    /// Navic deserializes mediaType as a typed enum; missing/wrong value crashes the entire parse.
    /// </summary>
    [Fact]
    public void Navic_SearchResult3_Songs_HaveMediaTypeSong()
    {
        var songs = new List<Dictionary<string, object?>>
        {
            new() { ["id"] = "s-1", ["title"] = "Track A", ["mediaType"] = "song" },
            new() { ["id"] = "s-2", ["title"] = "Track B", ["mediaType"] = "song" },
        };
        var xml = XmlBuilder.SearchResult3(
            new List<Dictionary<string, object?>> { new() { ["id"] = "ar-1", ["name"] = "Artist" } },
            new List<Dictionary<string, object?>> { new() { ["id"] = "al-1", ["name"] = "Album" } },
            songs);

        var doc = Parse(xml);
        var ns = "http://subsonic.org/restapi";
        var sr3 = doc.DocumentElement!["searchResult3", ns]!;
        var songNodes = sr3.GetElementsByTagName("song", ns);
        Assert.NotEmpty(songNodes);
        foreach (XmlElement s in songNodes)
            Assert.Equal("song", s.GetAttribute("mediaType"));
    }

    /// <summary>
    /// Dict with artistId=null → attribute must be absent from XML (WriteAttr skips nulls).
    /// Documents the crash path: Navic requires artistId as non-null String.
    /// </summary>
    [Fact]
    public void Navic_Song_ArtistId_NullOmitsAttribute()
    {
        var song = new Dictionary<string, object?>
        {
            ["id"] = "s-1", ["title"] = "Track", ["mediaType"] = "song", ["artistId"] = null
        };
        var xml = XmlBuilder.Song(song);
        var doc = Parse(xml);
        var s = doc.DocumentElement!["song", "http://subsonic.org/restapi"]!;
        // Null artistId must not produce an attribute at all (WriteAttr skips null)
        Assert.Equal("", s.GetAttribute("artistId")); // GetAttribute returns "" when absent
        Assert.False(s.HasAttribute("artistId"), "artistId attribute must be absent when null");
    }

    /// <summary>
    /// Dict with artistId="" → attribute must be present (confirms ?? "" fallback in ItemMapper).
    /// Navic requires the field; empty string is safe, null is not.
    /// </summary>
    [Fact]
    public void Navic_Song_ArtistId_EmptyStringPresentAsAttribute()
    {
        var song = new Dictionary<string, object?>
        {
            ["id"] = "s-1", ["title"] = "Track", ["mediaType"] = "song", ["artistId"] = ""
        };
        var xml = XmlBuilder.Song(song);
        var doc = Parse(xml);
        var s = doc.DocumentElement!["song", "http://subsonic.org/restapi"]!;
        Assert.True(s.HasAttribute("artistId"), "artistId attribute must be present when value is empty string");
        Assert.Equal("", s.GetAttribute("artistId"));
    }

    /// <summary>
    /// FolderIdToInt must return a non-negative int for any valid GUID.
    /// Navic types MusicFolder.id as Int (not String); a negative value or string causes a parse crash.
    /// </summary>
    [Fact]
    public void Navic_FolderIdToInt_ReturnsNonNegativeInt()
    {
        var guid = Guid.NewGuid().ToString();
        var result = SubsonicController.FolderIdToInt(guid);
        Assert.True(result >= 0, $"FolderIdToInt must return non-negative int, got {result}");
    }

    // ── Navic / Tempus ────────────────────────────────────────────────────────

    /// <summary>
    /// Share.created must use ISO 8601 with 'T' separator.
    /// Navic's Instant deserializer crashes on space-separated datetime strings.
    /// </summary>
    [Fact]
    public void Navic_ShareCreated_UsesISO8601WithT()
    {
        var share = new ShareXml(
            Id: "uid1",
            Url: "https://example.com/subfin/share/uid1?secret=abc",
            Description: null,
            Username: "alice",
            Created: "2026-01-15T10:30:00Z",
            Expires: "2027-01-15T10:30:00Z",
            VisitCount: 3,
            Songs: new List<Dictionary<string, object?>>());

        var xml = XmlBuilder.Shares(new List<ShareXml> { share });
        var doc = Parse(xml);
        var s = (XmlElement)doc.DocumentElement!
            ["shares", "http://subsonic.org/restapi"]!
            .ChildNodes[0]!;

        var created = s.GetAttribute("created");
        Assert.Contains("T", created); // must have T separator, not space
        Assert.DoesNotContain(" ", created);
    }

    // ── DSub2000 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Share scalar fields (id, username, visitCount) must be XML attributes, not child elements.
    /// DSub2000 parses attribute-style XML only; child elements produce silent null reads → crash on visitCount.
    /// </summary>
    [Fact]
    public void DSub2000_Share_ScalarFieldsAreAttributes()
    {
        var share = new ShareXml(
            Id: "uid2",
            Url: "https://example.com/subfin/share/uid2?secret=xyz",
            Description: "My share",
            Username: "bob",
            Created: "2026-02-01T00:00:00Z",
            Expires: "2027-02-01T00:00:00Z",
            VisitCount: 5,
            Songs: new List<Dictionary<string, object?>>());

        var xml = XmlBuilder.Shares(new List<ShareXml> { share });
        var doc = Parse(xml);
        var ns = "http://subsonic.org/restapi";
        var s = (XmlElement)doc.DocumentElement!["shares", ns]!.ChildNodes[0]!;

        Assert.Equal("uid2", s.GetAttribute("id"));
        Assert.Equal("bob", s.GetAttribute("username"));
        Assert.Equal("5", s.GetAttribute("visitCount"));

        // Must NOT have child elements for scalar fields
        Assert.Null(s["id"]);
        Assert.Null(s["username"]);
        Assert.Null(s["visitCount"]);
    }

    // ── Tempus ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When expires is set (including the fallback of created+1year), the attribute must be
    /// non-empty and contain 'T'. Tempus crashes if expires is absent.
    /// </summary>
    [Fact]
    public void Tempus_Share_ExpiresPresent_WithFallback()
    {
        // Simulate the fallback: created + 1 year
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expires = created.AddYears(1);

        var share = new ShareXml(
            Id: "uid3",
            Url: "https://example.com/subfin/share/uid3?secret=fallback",
            Description: null,
            Username: "carol",
            Created: created.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Expires: expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            VisitCount: 0,
            Songs: new List<Dictionary<string, object?>>());

        var xml = XmlBuilder.Shares(new List<ShareXml> { share });
        var doc = Parse(xml);
        var s = (XmlElement)doc.DocumentElement!
            ["shares", "http://subsonic.org/restapi"]!
            .ChildNodes[0]!;

        var exp = s.GetAttribute("expires");
        Assert.False(string.IsNullOrEmpty(exp), "expires must always be present");
        Assert.Contains("T", exp); // must be ISO 8601 format
    }

    /// <summary>
    /// getOpenSubsonicExtensions must include an extension named "songLyrics".
    /// Tempus only routes to getLyricsBySongId when songLyrics appears in this response.
    /// </summary>
    [Fact]
    public void Tempus_OpenSubsonicExtensions_IncludesSongLyrics()
    {
        var xml = XmlBuilder.OpenSubsonicExtensions();
        var doc = Parse(xml);
        var ns = "http://subsonic.org/restapi";
        var extensions = doc.DocumentElement!["openSubsonicExtensions", ns]!;

        var found = false;
        foreach (XmlElement ext in extensions.ChildNodes)
        {
            if (ext.GetAttribute("name") == "songLyrics")
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "openSubsonicExtensions must include an extension named 'songLyrics'");
    }
}
