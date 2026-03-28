using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.Subsonic.Response;

/// <summary>
/// Builds attribute-style Subsonic XML responses.
/// Subsonic XML uses attributes on elements, never child elements for scalar values.
/// </summary>
public static class XmlBuilder
{
    private const string Ns = "http://subsonic.org/restapi";

    /// <summary>Wrap a payload-building action in a subsonic-response envelope.</summary>
    public static string OkEnvelope(Action<XmlWriter> writePayload)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false, OmitXmlDeclaration = true };
        using var w = XmlWriter.Create(sb, settings);
        w.WriteStartElement("subsonic-response", Ns);
        w.WriteAttributeString("status", "ok");
        w.WriteAttributeString("version", SubsonicConstants.Version);
        w.WriteAttributeString("type", SubsonicConstants.ServerType);
        w.WriteAttributeString("serverVersion", SubsonicConstants.ServerVersion);
        w.WriteAttributeString("openSubsonic", "true");
        writePayload(w);
        w.WriteEndElement();
        w.Flush();
        return sb.ToString();
    }

    /// <summary>Error envelope.</summary>
    public static string ErrorEnvelope(int code, string message)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false, OmitXmlDeclaration = true };
        using var w = XmlWriter.Create(sb, settings);
        w.WriteStartElement("subsonic-response", Ns);
        w.WriteAttributeString("status", "failed");
        w.WriteAttributeString("version", SubsonicConstants.Version);
        w.WriteStartElement("error", Ns);
        w.WriteAttributeString("code", code.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("message", message);
        w.WriteEndElement();
        w.WriteEndElement();
        w.Flush();
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static void WriteAttr(XmlWriter w, string name, object? value)
    {
        if (value == null) return;
        w.WriteAttributeString(name, value switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        });
    }

    public static void WriteDictAsElement(XmlWriter w, string elementName, Dictionary<string, object?> d)
    {
        w.WriteStartElement(elementName, Ns);
        foreach (var kv in d)
        {
            if (kv.Value is List<Dictionary<string, object?>> children)
            {
                // child element list (e.g. song[] inside album)
                // determined by context; skip at this level — handled by callers
                continue;
            }
            WriteAttr(w, kv.Key, kv.Value);
        }
        w.WriteEndElement();
    }

    // ── Ping / License / ScanStatus / User ──────────────────────────────────

    public static string Ping() => OkEnvelope(_ => { });

    public static string ScanStatus() => OkEnvelope(w =>
    {
        w.WriteStartElement("scanStatus", Ns);
        w.WriteAttributeString("scanning", "false");
        w.WriteAttributeString("count", "0");
        w.WriteEndElement();
    });

    public static string User(string username) => OkEnvelope(w =>
    {
        w.WriteStartElement("user", Ns);
        w.WriteAttributeString("username", username);
        w.WriteAttributeString("email", "");
        w.WriteAttributeString("scrobblingEnabled", "false");
        w.WriteAttributeString("adminRole", "false");
        w.WriteAttributeString("settingsRole", "false");
        w.WriteAttributeString("downloadRole", "true");
        w.WriteAttributeString("uploadRole", "false");
        w.WriteAttributeString("playlistRole", "true");
        w.WriteAttributeString("coverArtRole", "false");
        w.WriteAttributeString("commentRole", "false");
        w.WriteAttributeString("podcastRole", "false");
        w.WriteAttributeString("streamRole", "true");
        w.WriteAttributeString("jukeboxRole", "false");
        w.WriteAttributeString("shareRole", "false");
        w.WriteAttributeString("videoConversionRole", "false");
        w.WriteEndElement();
    });

    public static string License() => OkEnvelope(w =>
    {
        w.WriteStartElement("license", Ns);
        w.WriteAttributeString("valid", "true");
        w.WriteAttributeString("email", "");
        w.WriteAttributeString("licenseExpires", "2099-01-01T00:00:00.000Z");
        w.WriteEndElement();
    });

    public static string OpenSubsonicExtensions() => OkEnvelope(w =>
    {
        w.WriteStartElement("openSubsonicExtensions", Ns);
        foreach (var (name, versions) in new[] { ("template", new[] { 1 }), ("transcodeOffset", new[] { 1 }) })
        {
            w.WriteStartElement("extension", Ns);
            w.WriteAttributeString("name", name);
            foreach (var v in versions)
            {
                w.WriteStartElement("version", Ns);
                w.WriteAttributeString("value", v.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Music Folders ────────────────────────────────────────────────────────

    public static string MusicFolders(List<(string Id, string Name)> folders) => OkEnvelope(w =>
    {
        w.WriteStartElement("musicFolders", Ns);
        foreach (var (id, name) in folders)
        {
            w.WriteStartElement("musicFolder", Ns);
            w.WriteAttributeString("id", id);
            w.WriteAttributeString("name", name);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Artists / Indexes ────────────────────────────────────────────────────

    public static string Artists(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index, string ignoredArticles = "The An A Die Das Ein Eine Les Le La") => OkEnvelope(w =>
    {
        w.WriteStartElement("artists", Ns);
        w.WriteAttributeString("ignoredArticles", ignoredArticles);
        foreach (var (letter, artists) in index)
        {
            w.WriteStartElement("index", Ns);
            w.WriteAttributeString("name", letter);
            foreach (var (id, name, albumCount) in artists)
            {
                w.WriteStartElement("artist", Ns);
                w.WriteAttributeString("id", id);
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("coverArt", $"ar-{id}");
                w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // Same structure as Artists but uses <indexes> root
    public static string Indexes(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index, string ignoredArticles = "The An A Die Das Ein Eine Les Le La", long lastModified = 0) => OkEnvelope(w =>
    {
        w.WriteStartElement("indexes", Ns);
        w.WriteAttributeString("lastModified", lastModified.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("ignoredArticles", ignoredArticles);
        foreach (var (letter, artists) in index)
        {
            w.WriteStartElement("index", Ns);
            w.WriteAttributeString("name", letter);
            foreach (var (id, name, albumCount) in artists)
            {
                w.WriteStartElement("artist", Ns);
                w.WriteAttributeString("id", id);
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("coverArt", $"ar-{id}");
                w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Artist with albums ───────────────────────────────────────────────────

    public static string Artist(Dictionary<string, object?> artist) => OkEnvelope(w =>
    {
        w.WriteStartElement("artist", Ns);
        foreach (var kv in artist)
        {
            if (kv.Key == "album" && kv.Value is List<Dictionary<string, object?>> albums)
            {
                foreach (var album in albums)
                {
                    w.WriteStartElement("album", Ns);
                    WriteAlbumID3Attrs(w, album);
                    w.WriteEndElement();
                }
            }
            else WriteAttr(w, kv.Key, kv.Value);
        }
        w.WriteEndElement();
    });

    // ── Album with songs ─────────────────────────────────────────────────────

    public static string Album(Dictionary<string, object?> album) => OkEnvelope(w =>
    {
        w.WriteStartElement("album", Ns);
        foreach (var kv in album)
        {
            if (kv.Key == "song" && kv.Value is List<Dictionary<string, object?>> songs)
            {
                foreach (var song in songs)
                {
                    w.WriteStartElement("song", Ns);
                    WriteSongAttrs(w, song);
                    w.WriteEndElement();
                }
            }
            else WriteAttr(w, kv.Key, kv.Value);
        }
        w.WriteEndElement();
    });

    // ── Song ─────────────────────────────────────────────────────────────────

    public static string Song(Dictionary<string, object?> song) => OkEnvelope(w =>
    {
        w.WriteStartElement("song", Ns);
        WriteSongAttrs(w, song);
        w.WriteEndElement();
    });

    // ── Directory ────────────────────────────────────────────────────────────

    public static string MusicDirectory(string id, string name, string? parent, IEnumerable<Dictionary<string, object?>> children) => OkEnvelope(w =>
    {
        w.WriteStartElement("directory", Ns);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("name", name);
        if (parent != null) w.WriteAttributeString("parent", parent);
        foreach (var child in children)
        {
            var isDir = child.TryGetValue("isDir", out var isd) && isd is bool b && b;
            w.WriteStartElement("child", Ns);
            if (isDir) WriteAlbumShortAttrs(w, child);
            else WriteSongAttrs(w, child);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Search ───────────────────────────────────────────────────────────────

    public static string SearchResult3(
        List<Dictionary<string, object?>> artists,
        List<Dictionary<string, object?>> albums,
        List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("searchResult3", Ns);
        foreach (var a in artists) { w.WriteStartElement("artist", Ns); WriteAlbumShortAttrs(w, a); w.WriteEndElement(); }
        foreach (var a in albums) { w.WriteStartElement("album", Ns); WriteAlbumID3Attrs(w, a); w.WriteEndElement(); }
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Album lists ──────────────────────────────────────────────────────────

    public static string AlbumList(List<Dictionary<string, object?>> albums, bool list2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(list2 ? "albumList2" : "albumList", Ns);
        foreach (var album in albums) { w.WriteStartElement("album", Ns); if (list2) WriteAlbumID3Attrs(w, album); else WriteAlbumShortAttrs(w, album); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string RandomSongs(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("randomSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string SongsByGenre(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("songsByGenre", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string TopSongs(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("topSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string SimilarSongs(List<Dictionary<string, object?>> songs, bool v2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(v2 ? "similarSongs2" : "similarSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Genres ───────────────────────────────────────────────────────────────

    public static string Genres(List<(string Name, int SongCount, int AlbumCount)> genres) => OkEnvelope(w =>
    {
        w.WriteStartElement("genres", Ns);
        foreach (var (name, songCount, albumCount) in genres)
        {
            w.WriteStartElement("genre", Ns);
            w.WriteAttributeString("value", name);
            w.WriteAttributeString("songCount", songCount.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Playlists ────────────────────────────────────────────────────────────

    public static string Playlists(List<Dictionary<string, object?>> playlists) => OkEnvelope(w =>
    {
        w.WriteStartElement("playlists", Ns);
        foreach (var pl in playlists) { w.WriteStartElement("playlist", Ns); WritePlaylistAttrs(w, pl, false); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string Playlist(Dictionary<string, object?> playlist) => OkEnvelope(w =>
    {
        w.WriteStartElement("playlist", Ns);
        WritePlaylistAttrs(w, playlist, true);
        w.WriteEndElement();
    });

    // ── Play queue ───────────────────────────────────────────────────────────

    public static string PlayQueue(string? currentId, int currentIndex, long positionMs, string? changedAt, string changedBy, List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("playQueue", Ns);
        if (currentId != null) w.WriteAttributeString("current", currentId);
        w.WriteAttributeString("position", positionMs.ToString(CultureInfo.InvariantCulture));
        if (changedAt != null) w.WriteAttributeString("changed", changedAt);
        w.WriteAttributeString("changedBy", changedBy);
        foreach (var s in songs) { w.WriteStartElement("entry", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Starred ──────────────────────────────────────────────────────────────

    public static string Starred(List<Dictionary<string, object?>> artists, List<Dictionary<string, object?>> albums, List<Dictionary<string, object?>> songs, bool v2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(v2 ? "starred2" : "starred", Ns);
        foreach (var a in artists) { w.WriteStartElement("artist", Ns); foreach (var kv in a) WriteAttr(w, kv.Key, kv.Value); w.WriteEndElement(); }
        foreach (var a in albums) { w.WriteStartElement("album", Ns); if (v2) WriteAlbumID3Attrs(w, a); else WriteAlbumShortAttrs(w, a); w.WriteEndElement(); }
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Shares ───────────────────────────────────────────────────────────────

    public static string Shares(List<ShareXml> shares) => OkEnvelope(w =>
    {
        w.WriteStartElement("shares", Ns);
        foreach (var share in shares) WriteShare(w, share);
        w.WriteEndElement();
    });

    public static string ShareCreated(ShareXml share) => OkEnvelope(w =>
    {
        w.WriteStartElement("shares", Ns);
        WriteShare(w, share);
        w.WriteEndElement();
    });

    private static void WriteShare(XmlWriter w, ShareXml s)
    {
        w.WriteStartElement("share", Ns);
        w.WriteAttributeString("id", s.Id);
        w.WriteAttributeString("url", s.Url);
        if (s.Description != null) w.WriteAttributeString("description", s.Description);
        w.WriteAttributeString("username", s.Username);
        w.WriteAttributeString("created", s.Created);
        w.WriteAttributeString("expires", s.Expires);
        w.WriteAttributeString("visitCount", s.VisitCount.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("songCount", s.Songs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var song in s.Songs) { w.WriteStartElement("entry", Ns); WriteSongAttrs(w, song); w.WriteEndElement(); }
        w.WriteEndElement();
    }

    // ── ArtistInfo ───────────────────────────────────────────────────────────

    public static string ArtistInfo(string? biography, string? musicBrainzId, string? lastFmUrl, string? artistImageUrl, List<Dictionary<string, object?>> similarArtists, bool v2 = false) => OkEnvelope(w =>
    {
        var imageUrl = artistImageUrl ?? "";
        w.WriteStartElement(v2 ? "artistInfo2" : "artistInfo", Ns);
        // Attributes first (required before any child elements per XmlWriter rules)
        if (!string.IsNullOrEmpty(musicBrainzId)) w.WriteAttributeString("musicBrainzId", musicBrainzId);
        if (!string.IsNullOrEmpty(lastFmUrl)) w.WriteAttributeString("lastFmUrl", lastFmUrl);
        w.WriteAttributeString("smallImageUrl", imageUrl);
        w.WriteAttributeString("mediumImageUrl", imageUrl);
        w.WriteAttributeString("largeImageUrl", imageUrl);
        // Text element children — DSub2000's ArtistInfoParser reads these as text elements, not attributes
        if (!string.IsNullOrEmpty(biography)) { w.WriteStartElement("biography", Ns); w.WriteString(biography); w.WriteEndElement(); }
        if (!string.IsNullOrEmpty(musicBrainzId)) { w.WriteStartElement("musicBrainzId", Ns); w.WriteString(musicBrainzId); w.WriteEndElement(); }
        if (!string.IsNullOrEmpty(lastFmUrl)) { w.WriteStartElement("lastFmUrl", Ns); w.WriteString(lastFmUrl); w.WriteEndElement(); }
        { w.WriteStartElement("smallImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        { w.WriteStartElement("mediumImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        { w.WriteStartElement("largeImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        foreach (var sa in similarArtists) { w.WriteStartElement("similarArtist", Ns); foreach (var kv in sa) WriteAttr(w, kv.Key, kv.Value); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── AlbumInfo ────────────────────────────────────────────────────────────

    public static string AlbumInfo(string? notes, string? musicBrainzId, string? lastFmUrl, bool v2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(v2 ? "albumInfo2" : "albumInfo", Ns);
        if (!string.IsNullOrEmpty(musicBrainzId)) w.WriteAttributeString("musicBrainzId", musicBrainzId);
        if (!string.IsNullOrEmpty(lastFmUrl)) w.WriteAttributeString("lastFmUrl", lastFmUrl);
        if (!string.IsNullOrEmpty(notes)) { w.WriteStartElement("notes", Ns); w.WriteString(notes); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── NowPlaying ───────────────────────────────────────────────────────────

    public static string NowPlaying(List<NowPlayingXml> entries) => OkEnvelope(w =>
    {
        w.WriteStartElement("nowPlaying", Ns);
        foreach (var e in entries)
        {
            w.WriteStartElement("entry", Ns);
            WriteSongAttrs(w, e.Song);
            w.WriteAttributeString("username", e.Username);
            w.WriteAttributeString("minutesAgo", e.MinutesAgo.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("playerId", e.PlayerId);
            w.WriteAttributeString("playerName", e.PlayerName);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Private attribute writers ────────────────────────────────────────────

    private static void WriteSongAttrs(XmlWriter w, Dictionary<string, object?> song)
    {
        foreach (var kv in song)
        {
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
    }

    private static void WriteAlbumShortAttrs(XmlWriter w, Dictionary<string, object?> album)
    {
        foreach (var kv in album)
        {
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
    }

    // AlbumID3 shape: no isDir (that's a Child field)
    private static void WriteAlbumID3Attrs(XmlWriter w, Dictionary<string, object?> album)
    {
        foreach (var kv in album)
        {
            if (kv.Key == "isDir") continue;
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
    }

    private static void WritePlaylistAttrs(XmlWriter w, Dictionary<string, object?> pl, bool includeSongs)
    {
        foreach (var kv in pl)
        {
            if (kv.Key == "entry" && kv.Value is List<Dictionary<string, object?>> songs)
            {
                if (includeSongs)
                    foreach (var s in songs) { w.WriteStartElement("entry", Ns); WriteSongAttrs(w, s); w.WriteEndElement(); }
            }
            else WriteAttr(w, kv.Key, kv.Value);
        }
    }
}

// Supporting types for XML builder
public record ShareXml(string Id, string Url, string? Description, string Username, string Created, string Expires, int VisitCount, List<Dictionary<string, object?>> Songs);
public record NowPlayingXml(Dictionary<string, object?> Song, string Username, int MinutesAgo, string PlayerId, string PlayerName);
