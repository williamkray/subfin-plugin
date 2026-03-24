using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Handles all /rest/* Subsonic API endpoints.
/// Returns XML by default; JSON when f=json is in the query.
/// </summary>
[ApiController]
[Route("rest")]
public class SubsonicController : ControllerBase
{
    private readonly SubsonicAuth _auth;
    private readonly ILibraryManager _library;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userData;
    private readonly ISessionManager _sessions;
    private readonly IPlaylistManager _playlists;
    private readonly ILogger<SubsonicController> _logger;

    public SubsonicController(
        SubsonicAuth auth,
        ILibraryManager library,
        IUserManager userManager,
        IUserDataManager userData,
        ISessionManager sessions,
        IPlaylistManager playlists,
        ILogger<SubsonicController> logger)
    {
        _auth = auth;
        _library = library;
        _userManager = userManager;
        _userData = userData;
        _sessions = sessions;
        _playlists = playlists;
        _logger = logger;
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    [HttpGet("{method}")]
    [HttpGet("{method}.view")]
    [HttpPost("{method}")]
    [HttpPost("{method}.view")]
    public async Task<IActionResult> Handle(string method)
    {
        var q = Request.Query;
        var format = q["f"].ToString().ToLowerInvariant() == "json" ? "json" : "xml";

        var config = SubsonicPlugin.Instance?.Configuration;
        if (config?.LogRestRequests == true)
            _logger.LogDebug("[Subsonic] {Method} {Format}", method, format);

        var m = method.ToLowerInvariant().TrimEnd();

        // Unauthenticated endpoints
        if (m is "ping" or "getlicense" or "getopensubsonicextensions")
            return Respond(format, HandleUnauthenticated(m));

        // Auth
        var authObj = _auth.Resolve(q);
        if (authObj is SubsonicAuth.AuthError err)
            return Respond(format, SubsonicEnvelope.Error(err.Code, err.Message), XmlBuilder.ErrorEnvelope(err.Code, err.Message));

        var auth = (AuthResult)authObj;
        var jellyfinUser = _userManager.GetUserById(Guid.Parse(auth.JellyfinUserId));
        if (jellyfinUser == null)
            return Respond(format, SubsonicEnvelope.Error(ErrorCode.WrongCredentials, "User not found."), XmlBuilder.ErrorEnvelope(ErrorCode.WrongCredentials, "User not found."));

        try
        {
            return await HandleAuthenticated(m, auth, jellyfinUser, q, format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Subsonic] Error handling {Method}", method);
            return Respond(format, SubsonicEnvelope.Error(ErrorCode.Generic, "Internal server error."), XmlBuilder.ErrorEnvelope(ErrorCode.Generic, "Internal server error."));
        }
    }

    // ── Unauthenticated ──────────────────────────────────────────────────────

    private static (Dictionary<string, object> Json, string Xml) HandleUnauthenticated(string method) => method switch
    {
        "ping" => (SubsonicEnvelope.Ok(), XmlBuilder.Ping()),
        "getlicense" => (SubsonicEnvelope.Ok(new() { ["license"] = new Dictionary<string, object> { ["valid"] = true, ["email"] = "", ["licenseExpires"] = "2099-01-01T00:00:00.000Z" } }), XmlBuilder.License()),
        "getopensubsonicextensions" => (SubsonicEnvelope.Ok(new() { ["openSubsonicExtensions"] = new[] { new Dictionary<string, object> { ["name"] = "template", ["versions"] = new[] { 1 } }, new Dictionary<string, object> { ["name"] = "transcodeOffset", ["versions"] = new[] { 1 } } } }), XmlBuilder.OpenSubsonicExtensions()),
        _ => (SubsonicEnvelope.Error(ErrorCode.NotFound, "Not found"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, "Not found"))
    };

    // ── Authenticated dispatch ───────────────────────────────────────────────

    private async Task<IActionResult> HandleAuthenticated(string method, AuthResult auth, User user, Microsoft.AspNetCore.Http.IQueryCollection q, string format)
    {
        var p = new QueryParams(q);

        return method switch
        {
            "getmusicfolders" => GetMusicFolders(auth, user, format),
            "getartists" => await GetArtists(auth, user, p, format),
            "getindexes" => await GetIndexes(auth, user, p, format),
            "getartist" => GetArtist(auth, user, p, format),
            "getalbum" => GetAlbum(auth, user, p, format),
            "getsong" => GetSong(p, format),
            "getmusicdirectory" => GetMusicDirectory(auth, user, p, format),
            "search3" or "search2" => Search3(auth, user, p, format),
            "getalbumlist" => GetAlbumList(auth, user, p, format, false),
            "getalbumlist2" => GetAlbumList(auth, user, p, format, true),
            "getrandomsongs" => GetRandomSongs(user, p, format),
            "getgenres" => GetGenres(user, format),
            "getsongsbygenre" => GetSongsByGenre(user, p, format),
            "getplaylists" => GetPlaylists(user, format),
            "getplaylist" => GetPlaylist(user, p, format),
            "createplaylist" => await CreatePlaylist(user, p, format),
            "updateplaylist" => await UpdatePlaylist(user, p, format),
            "deleteplaylist" => await DeletePlaylist(user, p, format),
            "star" => Star(user, p, format, true),
            "unstar" => Star(user, p, format, false),
            "setrating" => SetRating(user, p, format),
            "scrobble" => Scrobble(user, p, format),
            "getnowplaying" => GetNowPlaying(format),
            "saveplayqueue" => SavePlayQueue(auth, p, format),
            "getplayqueue" => GetPlayQueue(auth, format),
            "getshares" => GetShares(auth, user, format),
            "createshare" => CreateShare(auth, user, p, format),
            "updateshare" => UpdateShare(p, format),
            "deleteshare" => DeleteShare(p, format),
            "getstarred" => GetStarred(user, format, false),
            "getstarred2" => GetStarred(user, format, true),
            "getartistinfo" or "getartistinfo2" => GetArtistInfo(format, method.EndsWith("2")),
            "getalbuminfo" or "getalbuminfo2" => GetAlbumInfo(format, method.EndsWith("2")),
            "getsimilarsongs" or "getsimilarsongs2" => GetSimilarSongs(format, method.EndsWith("2")),
            "gettopsongs" => GetTopSongs(format),
            "getlyrics" => GetLyrics(format),
            "getlyricsbysongid" => GetLyricsBySongId(format),
            "stream" => await Stream(auth, p),
            "download" => Download(p),
            "getcoverart" => GetCoverArt(p),
            "getavatar" => GetAvatar(user),
            _ => Respond(format, SubsonicEnvelope.Error(ErrorCode.NotFound, $"Unknown method: {method}"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, $"Unknown method: {method}"))
        };
    }

    // ── Response helper ──────────────────────────────────────────────────────

    private IActionResult Respond(string format, Dictionary<string, object> json, string? xml = null)
    {
        if (format == "json")
            return new ContentResult { Content = JsonSerializer.Serialize(json), ContentType = "application/json; charset=utf-8", StatusCode = 200 };
        return new ContentResult { Content = xml ?? XmlBuilder.ErrorEnvelope(0, "XML not implemented"), ContentType = "text/xml; charset=utf-8", StatusCode = 200 };
    }

    private IActionResult Respond(string format, (Dictionary<string, object> Json, string Xml) tuple) =>
        Respond(format, tuple.Json, tuple.Xml);

    private IActionResult ErrorResponse(string format, int code, string message) =>
        Respond(format, SubsonicEnvelope.Error(code, message), XmlBuilder.ErrorEnvelope(code, message));

    // ── getMusicFolders ──────────────────────────────────────────────────────

    private IActionResult GetMusicFolders(AuthResult auth, User user, string format)
    {
        var saved = SubsonicStore.GetUserLibrarySettings(auth.SubsonicUsername);

        // Get music virtual folders from Jellyfin
        var virtualFolders = _library.GetVirtualFolders();
        var musicFolders = virtualFolders
            .Where(f => f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
            .Select(f => (f.ItemId, f.Name ?? ""))
            .ToList();

        if (saved.Count > 0)
            musicFolders = musicFolders.Where(f => saved.Contains(f.ItemId)).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["musicFolders"] = new Dictionary<string, object>
            {
                ["musicFolder"] = musicFolders.Select(f => new Dictionary<string, object> { ["id"] = f.ItemId, ["name"] = f.Item2 }).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.MusicFolders(musicFolders));
    }

    // ── getArtists / getIndexes ──────────────────────────────────────────────

    private async Task<IActionResult> GetArtists(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = await BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["artists"] = BuildArtistsJson(index) });
        return Respond(format, json, XmlBuilder.Artists(index));
    }

    private async Task<IActionResult> GetIndexes(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = await BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["indexes"] = BuildArtistsJson(index) });
        return Respond(format, json, XmlBuilder.Indexes(index));
    }

    private async Task<List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)>> BuildArtistIndex(
        AuthResult auth, User user, string? musicFolderId)
    {
        var folderIds = GetEffectiveFolderIds(auth, musicFolderId);
        var cacheKey = $"artistIndex:{auth.JellyfinUserId}:{(folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x)))}";
        const int TtlMs = 15 * 60 * 1000;

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null)
        {
            var ageMs = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(cached.CachedAt)).TotalMilliseconds;
            if (ageMs < TtlMs)
            {
                var parsed = JsonSerializer.Deserialize<List<ArtistCacheEntry>>(cached.ValueJson) ?? [];
                return GroupByLetter(parsed.Select(a => (a.Id, a.Name, a.AlbumCount)));
            }
        }

        var artists = BuildArtistList(user, folderIds);
        SubsonicStore.SetDerivedCache(cacheKey, JsonSerializer.Serialize(artists.Select(a => new ArtistCacheEntry(a.Id, a.Name, a.AlbumCount))), null);
        return GroupByLetter(artists);
    }

    private record ArtistCacheEntry(string Id, string Name, int AlbumCount);

    private List<(string Id, string Name, int AlbumCount)> BuildArtistList(User user, List<string>? folderIds)
    {
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
        };
        if (folderIds != null)
            query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var allAlbums = _library.GetItemList(query).OfType<MusicAlbum>();

        var byArtist = new Dictionary<string, (string Id, string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in allAlbums)
        {
            var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(artistName)) continue;
            var artistId = album.MusicArtist?.Id;
            if (!artistId.HasValue) continue;
            var key = artistName.ToLowerInvariant().Replace(" ", "");
            if (byArtist.TryGetValue(key, out var existing))
                byArtist[key] = existing with { Count = existing.Count + 1 };
            else
                byArtist[key] = (artistId.Value.ToString("N"), artistName, 1);
        }
        return byArtist.Values.Select(v => (v.Id, v.Name, v.Count)).OrderBy(v => v.Name).ToList();
    }

    private static List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> GroupByLetter(
        IEnumerable<(string Id, string Name, int AlbumCount)> artists)
    {
        var grouped = new SortedDictionary<string, List<(string, string, int)>>();
        foreach (var a in artists)
        {
            var letter = ItemMapper.IndexLetter(a.Name);
            if (!grouped.ContainsKey(letter)) grouped[letter] = [];
            grouped[letter].Add(a);
        }
        return grouped.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static Dictionary<string, object> BuildArtistsJson(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index) => new()
    {
        ["ignoredArticles"] = "The An A Die Das Ein Eine Les Le La",
        ["index"] = index.Select(g => new Dictionary<string, object>
        {
            ["name"] = g.Letter,
            ["artist"] = g.Artists.Select(a => new Dictionary<string, object>
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["coverArt"] = $"ar-{a.Id}",
                ["albumCount"] = a.AlbumCount,
            }).ToList()
        }).ToList()
    };

    // ── getArtist ────────────────────────────────────────────────────────────

    private IActionResult GetArtist(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");

        var rawId = ItemMapper.StripPrefix(id);
        if (!Guid.TryParse(rawId, out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var artist = _library.GetItemById<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            AlbumArtistIds = [guid],
            Recursive = true,
        };
        var folderIds = GetEffectiveFolderIds(auth, null);
        if (folderIds != null) query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().ToList();

        var mapped = ItemMapper.ToArtistWithAlbums(artist, albums);
        var json = SubsonicEnvelope.Ok(new() { ["artist"] = mapped });
        return Respond(format, json, XmlBuilder.Artist(mapped));
    }

    // ── getAlbum ─────────────────────────────────────────────────────────────

    private IActionResult GetAlbum(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");

        var rawId = ItemMapper.StripPrefix(id);
        if (!Guid.TryParse(rawId, out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var album = _library.GetItemById<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = guid,
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();

        var mapped = ItemMapper.ToAlbum(album, songs);
        var json = SubsonicEnvelope.Ok(new() { ["album"] = mapped });
        return Respond(format, json, XmlBuilder.Album(mapped));
    }

    // ── getSong ──────────────────────────────────────────────────────────────

    private IActionResult GetSong(QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var song = _library.GetItemById<Audio>(guid);
        if (song == null) return ErrorResponse(format, ErrorCode.NotFound, "Song not found");

        var mapped = ItemMapper.ToSong(song);
        var json = SubsonicEnvelope.Ok(new() { ["song"] = mapped });
        return Respond(format, json, XmlBuilder.Song(mapped));
    }

    // ── getMusicDirectory ────────────────────────────────────────────────────

    private IActionResult GetMusicDirectory(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");

        var rawId = ItemMapper.StripPrefix(id);
        if (!Guid.TryParse(rawId, out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var item = _library.GetItemById(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var children = _library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = guid,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        });

        var childMaps = children.Select(c => c switch
        {
            MusicAlbum album => ItemMapper.ToAlbumShort(album),
            Audio song => ItemMapper.ToSong(song),
            _ => null
        }).Where(c => c != null).Cast<Dictionary<string, object?>>().ToList();

        var parentId = item.ParentId == Guid.Empty ? null : item.ParentId.ToString("N");
        var json = SubsonicEnvelope.Ok(new()
        {
            ["directory"] = new Dictionary<string, object>
            {
                ["id"] = guid.ToString("N"),
                ["name"] = item.Name ?? "",
                ["parent"] = (object?)parentId ?? "",
                ["child"] = childMaps,
            }
        });
        return Respond(format, json, XmlBuilder.MusicDirectory(guid.ToString("N"), item.Name ?? "", parentId, childMaps));
    }

    // ── search3 ──────────────────────────────────────────────────────────────

    private IActionResult Search3(AuthResult auth, User user, QueryParams p, string format)
    {
        var query = p.Get("query") ?? "";
        var artistCount = p.GetInt("artistCount", 20);
        var albumCount = p.GetInt("albumCount", 20);
        var songCount = p.GetInt("songCount", 20);
        var artistOffset = p.GetInt("artistOffset", 0);
        var albumOffset = p.GetInt("albumOffset", 0);
        var songOffset = p.GetInt("songOffset", 0);

        var artists = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.MusicArtist],
            Limit = artistCount,
            StartIndex = artistOffset,
            Recursive = true,
        }).OfType<MusicArtist>().Select(a => ItemMapper.ToArtist(a)).ToList();

        var albums = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = albumCount,
            StartIndex = albumOffset,
            Recursive = true,
        }).OfType<MusicAlbum>().Select(ItemMapper.ToAlbumShort).ToList();

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.Audio],
            Limit = songCount,
            StartIndex = songOffset,
            Recursive = true,
        }).OfType<Audio>().Select(s => ItemMapper.ToSong(s)).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["searchResult3"] = new Dictionary<string, object>
            {
                ["artist"] = artists,
                ["album"] = albums,
                ["song"] = songs,
            }
        });
        return Respond(format, json, XmlBuilder.SearchResult3(artists, albums, songs));
    }

    // ── getAlbumList / getAlbumList2 ─────────────────────────────────────────

    private IActionResult GetAlbumList(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var type = p.Get("type") ?? "alphabeticalByName";
        var size = Math.Min(p.GetInt("size", 10), 500);
        var offset = p.GetInt("offset", 0);

        var (sortBy, sortOrder) = type switch
        {
            "newest" => (ItemSortBy.DateCreated, SortOrder.Descending),
            "alphabeticalByName" => (ItemSortBy.SortName, SortOrder.Ascending),
            "alphabeticalByArtist" => (ItemSortBy.AlbumArtist, SortOrder.Ascending),
            "random" => (ItemSortBy.Random, SortOrder.Ascending),
            "highest" => (ItemSortBy.CommunityRating, SortOrder.Descending),
            "frequent" => (ItemSortBy.PlayCount, SortOrder.Descending),
            "recent" => (ItemSortBy.DatePlayed, SortOrder.Descending),
            _ => (ItemSortBy.SortName, SortOrder.Ascending),
        };

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            OrderBy = [(sortBy, sortOrder)],
            Limit = size,
            StartIndex = offset,
            Recursive = true,
        };

        if (type == "starred") { query.IsFavorite = true; }
        if (type == "byYear")
        {
            var fromYear = p.GetInt("fromYear", 0);
            var toYear = p.GetInt("toYear", 9999);
            query.Years = Enumerable.Range(fromYear, Math.Max(1, toYear - fromYear + 1)).ToArray();
            query.OrderBy = [(ItemSortBy.ProductionYear, SortOrder.Ascending)];
        }
        if (type == "byGenre")
        {
            query.Genres = new List<string> { p.Get("genre") ?? "" };
        }

        var folderIds = GetEffectiveFolderIds(auth, p.MusicFolderId);
        if (folderIds != null) query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().Select(ItemMapper.ToAlbumShort).ToList();
        var json = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = albums } });
        return Respond(format, json, XmlBuilder.AlbumList(albums, v2));
    }

    // ── getRandomSongs ───────────────────────────────────────────────────────

    private IActionResult GetRandomSongs(User user, QueryParams p, string format)
    {
        var size = Math.Min(p.GetInt("size", 10), 500);
        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
            Limit = size,
            Recursive = true,
        }).OfType<Audio>().Select(s => ItemMapper.ToSong(s)).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["randomSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.RandomSongs(songs));
    }

    // ── getGenres ────────────────────────────────────────────────────────────

    private IActionResult GetGenres(User user, string format)
    {
        var genreResult = _library.GetGenres(new InternalItemsQuery(user));
        var genres = genreResult.Items.Select(g =>
        {
            var name = g.Item1.Name ?? "";
            var songCount = _library.GetCount(new InternalItemsQuery(user)
            {
                Genres = new List<string> { name },
                IncludeItemTypes = [BaseItemKind.Audio],
                Recursive = true,
            });
            var albumCount = _library.GetCount(new InternalItemsQuery(user)
            {
                Genres = new List<string> { name },
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true,
            });
            return (name, songCount, albumCount);
        }).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["genres"] = new Dictionary<string, object>
            {
                ["genre"] = genres.Select(g => new Dictionary<string, object> { ["value"] = g.name, ["songCount"] = g.songCount, ["albumCount"] = g.albumCount }).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.Genres(genres));
    }

    // ── getSongsByGenre ──────────────────────────────────────────────────────

    private IActionResult GetSongsByGenre(User user, QueryParams p, string format)
    {
        var genre = p.Get("genre") ?? "";
        var count = Math.Min(p.GetInt("count", 10), 500);
        var offset = p.GetInt("offset", 0);

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            Genres = new List<string> { genre },
            IncludeItemTypes = [BaseItemKind.Audio],
            Limit = count,
            StartIndex = offset,
            Recursive = true,
        }).OfType<Audio>().Select(s => ItemMapper.ToSong(s)).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["songsByGenre"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.SongsByGenre(songs));
    }

    // ── getPlaylists ─────────────────────────────────────────────────────────

    private IActionResult GetPlaylists(User user, string format)
    {
        var playlists = _playlists.GetPlaylists(user.Id)
            .Select(pl => MapPlaylist(pl, user, false)).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["playlists"] = new Dictionary<string, object> { ["playlist"] = playlists } });
        return Respond(format, json, XmlBuilder.Playlists(playlists));
    }

    // ── getPlaylist ──────────────────────────────────────────────────────────

    private IActionResult GetPlaylist(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");

        var rawId = ItemMapper.StripPrefix(id);
        if (!Guid.TryParse(rawId, out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var pl = _library.GetItemById<Playlist>(guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, XmlBuilder.Playlist(mapped));
    }

    private Dictionary<string, object?> MapPlaylist(Playlist pl, User user, bool includeSongs)
    {
        var changed = pl.DateLastMediaAdded ?? pl.DateCreated;
        var songs = includeSongs
            ? _library.GetItemList(new InternalItemsQuery(user) { ParentId = pl.Id }).OfType<Audio>()
                .Select(s => ItemMapper.ToSong(s)).ToList()
            : new List<Dictionary<string, object?>>();

        return new()
        {
            ["id"] = $"pl-{pl.Id:N}",
            ["name"] = pl.Name ?? "",
            ["comment"] = pl.Overview ?? "",
            ["owner"] = user.Username,
            ["public"] = true,
            ["songCount"] = pl.LinkedChildren?.Length ?? 0,
            ["duration"] = songs.Sum(s => s.TryGetValue("duration", out var d) ? d is int i ? i : 0 : 0),
            ["created"] = pl.DateCreated.ToString("o"),
            ["changed"] = (changed == default ? pl.DateCreated : changed).ToString("o"),
            ["coverArt"] = $"pl-{pl.Id:N}",
            ["entry"] = songs,
        };
    }

    // ── createPlaylist / updatePlaylist / deletePlaylist ─────────────────────

    private async Task<IActionResult> CreatePlaylist(User user, QueryParams p, string format)
    {
        var name = p.Get("name") ?? "New Playlist";
        var songIds = Request.Query["songId"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var itemIds = songIds.Select(s => { Guid.TryParse(ItemMapper.StripPrefix(s), out var g); return g; }).Where(g => g != Guid.Empty).ToArray();

        var result = await _playlists.CreatePlaylist(new MediaBrowser.Model.Playlists.PlaylistCreationRequest
        {
            Name = name,
            ItemIdList = itemIds,
            UserId = user.Id,
        });

        var pl = _library.GetItemById<Playlist>(Guid.Parse(result.Id));
        if (pl == null) return ErrorResponse(format, ErrorCode.Generic, "Failed to create playlist");

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, XmlBuilder.Playlist(mapped));
    }

    private async Task<IActionResult> UpdatePlaylist(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing playlistId");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var pl = _library.GetItemById<Playlist>(guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        var name = p.Get("name");
        if (!string.IsNullOrEmpty(name)) pl.Name = name;
        await _library.UpdateItemAsync(pl, pl.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None);

        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private async Task<IActionResult> DeletePlaylist(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var pl = _library.GetItemById<Playlist>(guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        _library.DeleteItem(pl, new MediaBrowser.Controller.Library.DeleteOptions());
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── star / unstar ────────────────────────────────────────────────────────

    private IActionResult Star(User user, QueryParams p, string format, bool star)
    {
        var ids = Request.Query["id"].Concat(Request.Query["albumId"]).Concat(Request.Query["artistId"])
            .Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();

        foreach (var id in ids)
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) continue;
            var item = _library.GetItemById(guid);
            if (item == null) continue;
            var data = _userData.GetUserData(user, item);
            data.IsFavorite = star;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── setRating ────────────────────────────────────────────────────────────

    private IActionResult SetRating(User user, QueryParams p, string format)
    {
        var id = p.Id;
        var rating = p.GetInt("rating", 0);
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var item = _library.GetItemById(guid);
        if (item == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var data = _userData.GetUserData(user, item);
        data.Rating = rating > 0 ? rating : null;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── scrobble ─────────────────────────────────────────────────────────────

    private IActionResult Scrobble(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var item = _library.GetItemById<Audio>(guid);
        if (item == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var data = _userData.GetUserData(user, item);
        data.PlayCount++;
        data.LastPlayedDate = DateTimeOffset.UtcNow.UtcDateTime;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── getNowPlaying ────────────────────────────────────────────────────────

    private IActionResult GetNowPlaying(string format)
    {
        var sessions = _sessions.Sessions.Where(s => s.NowPlayingItem != null).ToList();
        var entries = sessions.Select(s =>
        {
            var item = s.NowPlayingItem;
            if (item == null) return null;
            var itemId = item.Id.ToString("N");
            var song = new Dictionary<string, object?>
            {
                ["id"] = itemId,
                ["title"] = item.Name ?? "",
                ["parent"] = item.ParentId?.ToString("N") ?? "",
                ["isDir"] = false,
                ["isVideo"] = false,
                ["type"] = "music",
                ["mediaType"] = "audio",
                ["duration"] = ItemMapper.TicksToSeconds(item.RunTimeTicks),
                ["artist"] = item.Artists?.FirstOrDefault() ?? "",
                ["album"] = item.Album ?? "",
                ["coverArt"] = itemId,
            };
            var minutesAgo = (int)(DateTime.UtcNow - s.LastActivityDate).TotalMinutes;
            return new NowPlayingXml(song, s.UserName ?? "", minutesAgo, s.Id, s.DeviceName ?? "");
        }).Where(e => e != null).Cast<NowPlayingXml>().ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["nowPlaying"] = new Dictionary<string, object>
            {
                ["entry"] = entries.Select(e => e.Song).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.NowPlaying(entries));
    }

    // ── savePlayQueue / getPlayQueue ─────────────────────────────────────────

    private IActionResult SavePlayQueue(AuthResult auth, QueryParams p, string format)
    {
        var ids = Request.Query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var current = p.Get("current");
        var position = p.GetLong("position", 0);
        SubsonicStore.SavePlayQueue(auth.SubsonicUsername, ids, current, 0, position, "");
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult GetPlayQueue(AuthResult auth, string format)
    {
        var pq = SubsonicStore.GetPlayQueue(auth.SubsonicUsername);
        if (pq == null) return Respond(format, SubsonicEnvelope.Ok(new() { ["playQueue"] = new Dictionary<string, object>() }),
            XmlBuilder.OkEnvelope(w => { w.WriteStartElement("playQueue", "http://subsonic.org/restapi"); w.WriteEndElement(); }));

        var songs = pq.EntryIds.Select(id =>
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return null;
            var audio = _library.GetItemById<Audio>(guid);
            return audio != null ? ItemMapper.ToSong(audio) : null;
        }).Where(s => s != null).Cast<Dictionary<string, object?>>().ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["playQueue"] = new Dictionary<string, object>
            {
                ["current"] = pq.CurrentId ?? "",
                ["position"] = pq.PositionMs,
                ["changed"] = pq.ChangedAt ?? "",
                ["changedBy"] = pq.ChangedBy,
                ["entry"] = songs,
            }
        });
        return Respond(format, json, XmlBuilder.PlayQueue(pq.CurrentId, pq.CurrentIndex, pq.PositionMs, pq.ChangedAt, pq.ChangedBy, songs));
    }

    // ── Shares ───────────────────────────────────────────────────────────────

    private IActionResult GetShares(AuthResult auth, User user, string format)
    {
        var shares = SubsonicStore.GetSharesForUser(auth.SubsonicUsername);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var xmlShares = shares.Select(s => BuildShareXml(s, baseUrl, user)).ToList();
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = xmlShares.Select(ShareToJson).ToList() } });
        return Respond(format, json, XmlBuilder.Shares(xmlShares));
    }

    private IActionResult CreateShare(AuthResult auth, User user, QueryParams p, string format)
    {
        var ids = Request.Query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var desc = p.Get("description");
        var expiresParam = p.Get("expires");
        string? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresParam) && long.TryParse(expiresParam, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");

        var devices = SubsonicStore.GetDevicesForUser(auth.SubsonicUsername);
        var device = devices.FirstOrDefault();
        if (device == null) return ErrorResponse(format, ErrorCode.Generic, "No linked device found. Link a device first.");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var uid = SubsonicStore.InsertShare(device.Id, ids, ids, desc, expiresAt, secret);

        var share = SubsonicStore.GetShare(uid)!;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var xmlShare = BuildShareXml(share, baseUrl, user);
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = new[] { ShareToJson(xmlShare) } } });
        return Respond(format, json, XmlBuilder.ShareCreated(xmlShare));
    }

    private IActionResult UpdateShare(QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        var desc = p.Get("description");
        var expiresParam = p.Get("expires");
        string? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresParam) && long.TryParse(expiresParam, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");
        SubsonicStore.UpdateShare(id, desc, expiresAt);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult DeleteShare(QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        SubsonicStore.DeleteShare(id);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private ShareXml BuildShareXml(ShareRecord s, string baseUrl, User user)
    {
        var secret = SubsonicStore.GetShareSecret(s.ShareUid) ?? "";
        var url = $"{baseUrl}/Subsonic/share/{s.ShareUid}?secret={Uri.EscapeDataString(secret)}";
        var expires = s.ExpiresAt ?? DateTimeOffset.Parse(s.CreatedAt).AddYears(1).ToString("o");
        var songs = s.EntryIdsFlat.Select(id =>
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return null;
            var audio = _library.GetItemById<Audio>(guid);
            return audio != null ? ItemMapper.ToSong(audio) : null;
        }).Where(x => x != null).Cast<Dictionary<string, object?>>().ToList();

        return new ShareXml(s.ShareUid, url, s.Description, user.Username, s.CreatedAt, expires, s.VisitCount, songs);
    }

    private static Dictionary<string, object> ShareToJson(ShareXml s) => new()
    {
        ["id"] = s.Id, ["url"] = s.Url, ["description"] = s.Description ?? "",
        ["username"] = s.Username, ["created"] = s.Created, ["expires"] = s.Expires,
        ["visitCount"] = s.VisitCount, ["songCount"] = s.Songs.Count,
        ["entry"] = s.Songs,
    };

    // ── getStarred / getStarred2 ─────────────────────────────────────────────

    private IActionResult GetStarred(User user, string format, bool v2)
    {
        var artists = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicArtist], IsFavorite = true, Recursive = true })
            .OfType<MusicArtist>().Select(a => new Dictionary<string, object?> { ["id"] = a.Id.ToString("N"), ["name"] = a.Name ?? "" }).ToList();
        var albums = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicAlbum], IsFavorite = true, Recursive = true })
            .OfType<MusicAlbum>().Select(ItemMapper.ToAlbumShort).ToList();
        var songs = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Audio], IsFavorite = true, Recursive = true })
            .OfType<Audio>().Select(s => ItemMapper.ToSong(s)).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            [v2 ? "starred2" : "starred"] = new Dictionary<string, object> { ["artist"] = artists, ["album"] = albums, ["song"] = songs }
        });
        return Respond(format, json, XmlBuilder.Starred(artists, albums, songs, v2));
    }

    // ── Stub endpoints ───────────────────────────────────────────────────────

    private IActionResult GetArtistInfo(string format, bool v2)
    {
        var json = SubsonicEnvelope.Ok(new()
        { [v2 ? "artistInfo2" : "artistInfo"] = new Dictionary<string, object> { ["similarArtist"] = new List<object>() } });
        return Respond(format, json, XmlBuilder.ArtistInfo(null, null, null, [], v2));
    }

    private IActionResult GetAlbumInfo(string format, bool v2)
    {
        var json = SubsonicEnvelope.Ok(new() { [v2 ? "albumInfo2" : "albumInfo"] = new Dictionary<string, object>() });
        return Respond(format, json, XmlBuilder.AlbumInfo(null, null, null, v2));
    }

    private IActionResult GetSimilarSongs(string format, bool v2)
    {
        var json = SubsonicEnvelope.Ok(new()
        { [v2 ? "similarSongs2" : "similarSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } });
        return Respond(format, json, XmlBuilder.OkEnvelope(w =>
        {
            w.WriteStartElement(v2 ? "similarSongs2" : "similarSongs", "http://subsonic.org/restapi");
            w.WriteEndElement();
        }));
    }

    private IActionResult GetTopSongs(string format)
    {
        var json = SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } });
        return Respond(format, json, XmlBuilder.TopSongs([]));
    }

    private IActionResult GetLyrics(string format)
    {
        var json = SubsonicEnvelope.Ok(new() { ["lyrics"] = new Dictionary<string, object>() });
        return Respond(format, json, XmlBuilder.OkEnvelope(w =>
        { w.WriteStartElement("lyrics", "http://subsonic.org/restapi"); w.WriteEndElement(); }));
    }

    private IActionResult GetLyricsBySongId(string format)
    {
        var json = SubsonicEnvelope.Ok(new()
        { ["lyricsList"] = new Dictionary<string, object> { ["structuredLyrics"] = new List<object>() } });
        return Respond(format, json, XmlBuilder.OkEnvelope(w =>
        { w.WriteStartElement("lyricsList", "http://subsonic.org/restapi"); w.WriteEndElement(); }));
    }

    // ── stream / download ────────────────────────────────────────────────────

    private async Task<IActionResult> Stream(AuthResult auth, QueryParams p)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return BadRequest();
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return NotFound();

        // Check share allowlist
        if (auth.ShareAllowedIds != null && !auth.ShareAllowedIds.Contains(guid.ToString("N")))
            return StatusCode(403);

        return Redirect($"/Audio/{guid:N}/universal?userId={auth.JellyfinUserId}&audioCodec=mp3&container=mp3");
    }

    private IActionResult Download(QueryParams p)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return BadRequest();
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return NotFound();

        var item = _library.GetItemById<Audio>(guid);
        if (item?.Path == null) return NotFound();

        return PhysicalFile(item.Path, "audio/mpeg", Path.GetFileName(item.Path), true);
    }

    // ── getCoverArt / getAvatar ──────────────────────────────────────────────

    private IActionResult GetCoverArt(QueryParams p)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return BadRequest();
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return NotFound();
        return Redirect($"/Items/{guid:N}/Images/Primary");
    }

    private IActionResult GetAvatar(User user) =>
        Redirect($"/Users/{user.Id}/Images/Primary");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<string>? GetEffectiveFolderIds(AuthResult auth, string? clientParam)
    {
        if (!string.IsNullOrEmpty(clientParam)) return [clientParam];
        var saved = SubsonicStore.GetUserLibrarySettings(auth.SubsonicUsername);
        return saved.Count == 0 ? null : saved;
    }
}

/// <summary>Thin wrapper over IQueryCollection for convenient param extraction.</summary>
public class QueryParams
{
    private readonly Microsoft.AspNetCore.Http.IQueryCollection _q;
    public QueryParams(Microsoft.AspNetCore.Http.IQueryCollection q) => _q = q;
    public string? Id => Get("id") ?? Get("playlistId");
    public string? MusicFolderId => Get("musicFolderId");
    public string? Get(string key) { var v = _q[key].ToString(); return string.IsNullOrEmpty(v) ? null : v; }
    public int GetInt(string key, int def) => int.TryParse(_q[key], out var v) ? v : def;
    public long GetLong(string key, long def) => long.TryParse(_q[key], out var v) ? v : def;
}
