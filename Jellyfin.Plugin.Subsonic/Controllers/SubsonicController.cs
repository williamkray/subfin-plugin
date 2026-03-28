using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMusicManager _musicManager;
    private readonly IAuthenticationManager _authManager;
    private readonly ILogger<SubsonicController> _logger;

    public SubsonicController(
        SubsonicAuth auth,
        ILibraryManager library,
        IUserManager userManager,
        IUserDataManager userData,
        ISessionManager sessions,
        IPlaylistManager playlists,
        IHttpClientFactory httpClientFactory,
        IMusicManager musicManager,
        IAuthenticationManager authManager,
        ILogger<SubsonicController> logger)
    {
        _auth = auth;
        _library = library;
        _userManager = userManager;
        _userData = userData;
        _sessions = sessions;
        _playlists = playlists;
        _httpClientFactory = httpClientFactory;
        _musicManager = musicManager;
        _authManager = authManager;
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
            _logger.LogInformation("[Subsonic] {Method} {Format}", method, format);

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

    private static (JsonObject Json, string Xml) HandleUnauthenticated(string method) => method switch
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
            "getartists" => GetArtists(auth, user, p, format),
            "getindexes" => GetIndexes(auth, user, p, format),
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
            "deleteplaylist" => DeletePlaylist(user, p, format),
            "star" => Star(user, p, format, true),
            "unstar" => Star(user, p, format, false),
            "setrating" => SetRating(user, p, format),
            "scrobble" => await Scrobble(auth, user, p, format),
            "getuser" or "getusers" => GetUser(user, format),
            "getscanstatus" => GetScanStatus(format),
            "getnowplaying" => GetNowPlaying(format),
            "saveplayqueue" => SavePlayQueue(auth, p, format),
            "getplayqueue" => GetPlayQueue(auth, format),
            "getshares" => GetShares(auth, user, format),
            "createshare" => CreateShare(auth, user, p, format),
            "updateshare" => UpdateShare(p, format),
            "deleteshare" => DeleteShare(p, format),
            "getstarred" => GetStarred(user, format, false),
            "getstarred2" => GetStarred(user, format, true),
            "getartistinfo" or "getartistinfo2" => await GetArtistInfo(auth, user, p, format, method.EndsWith("2")),
            "getalbuminfo" or "getalbuminfo2" => await GetAlbumInfo(auth, user, p, format, method.EndsWith("2")),
            "getsimilarsongs" or "getsimilarsongs2" => GetSimilarSongs(user, p, format, method.EndsWith("2")),
            "gettopsongs" => GetTopSongs(user, p, format),
            "getlyrics" => GetLyrics(format),
            "getlyricsbysongid" => GetLyricsBySongId(format),
            "stream" => await Stream(auth, p),
            "download" => Download(p),
            "getcoverart" => GetCoverArt(p),
            "getavatar" => GetAvatar(user),
            _ => Respond(format, SubsonicEnvelope.Error(ErrorCode.NotFound, $"Unknown method: {method}"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, $"Unknown method: {method}"))
        };
    }

    // ── Artist tag-entity resolution ─────────────────────────────────────────

    /// <summary>
    /// Resolves an artist name to the tag/index entity ID (the one AlbumArtistIds queries match).
    /// Never use album.MusicArtist?.Id — that's the folder-hierarchy entity and won't match.
    /// </summary>
    private string? ResolveArtistTagId(string? name) =>
        string.IsNullOrEmpty(name) ? null :
        _library.GetArtist(name) is MusicArtist a ? $"ar-{a.Id:N}" : null;

    // ── Response helper ──────────────────────────────────────────────────────

    private IActionResult Respond(string format, JsonObject json, string? xml = null)
    {
        if (format == "json")
            return new ContentResult { Content = json.ToJsonString(), ContentType = "application/json; charset=utf-8", StatusCode = 200 };
        return new ContentResult { Content = xml ?? XmlBuilder.ErrorEnvelope(0, "XML not implemented"), ContentType = "text/xml; charset=utf-8", StatusCode = 200 };
    }

    private IActionResult Respond(string format, (JsonObject Json, string Xml) tuple) =>
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
                ["musicFolder"] = musicFolders.Select(f => new Dictionary<string, object> { ["id"] = FolderIdToInt(f.ItemId), ["name"] = f.Item2 }).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.MusicFolders(musicFolders));
    }

    // ── getArtists / getIndexes ──────────────────────────────────────────────

    private IActionResult GetArtists(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["artists"] = BuildArtistsJson(index) });
        return Respond(format, json, XmlBuilder.Artists(index));
    }

    private IActionResult GetIndexes(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["indexes"] = BuildArtistsJson(index) });
        return Respond(format, json, XmlBuilder.Indexes(index));
    }

    private List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> BuildArtistIndex(
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

    private static readonly Regex _artistKeyRegex = new(@"[\s._/*'""\-]+", RegexOptions.Compiled);
    private static string CanonicalArtistKey(string name)
        => _artistKeyRegex.Replace(name.ToLowerInvariant(), "");

    private List<(string Id, string Name, int AlbumCount)> BuildArtistList(User user, List<string>? folderIds)
    {
        var albumQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
        };
        ApplyFolderScoping(albumQuery, folderIds);

        var allAlbums = _library.GetItemList(albumQuery).OfType<MusicAlbum>();

        // Use _library.GetArtist(name) — the same resolver Jellyfin uses when building
        // album DTOs — so the entity ID we return for each artist is always the one that
        // AlbumArtistIds queries will match. album.MusicArtist?.Id is unreliable because
        // it comes from the folder-hierarchy nav property and may point to a normalized
        // entity (e.g. "_NSYNC") that doesn't match the tag-based "ItemValues" index
        // (which stores "*NSYNC"), causing getArtist to return 0 albums.
        var byKey = new Dictionary<string, (string Id, string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in allAlbums)
        {
            var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(artistName)) continue;

            var key = CanonicalArtistKey(artistName);
            if (byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = existing with { Count = existing.Count + 1 };
                continue;
            }

            var artistEntity = _library.GetArtist(artistName);
            if (artistEntity == null) continue;

            byKey[key] = (artistEntity.Id.ToString("N"), artistName, 1);
        }
        return byKey.Values.Select(v => (v.Id, v.Name, v.Count)).OrderBy(v => v.Name).ToList();
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
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = _library.GetItemById<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var folderIds = GetEffectiveFolderIds(auth, null);

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            AlbumArtistIds = [guid],
            Recursive = true,
        };
        ApplyFolderScoping(query, folderIds);

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().ToList();
        _logger.LogInformation("[Subsonic] getArtist {Name} (guid={Guid}): {Count} albums", artist.Name, guid, albums.Count);

        // Fallback: if no albums found, re-resolve via GetArtist(name) — the guid we received
        // may be a folder/hierarchy entity (whose ID doesn't work with AlbumArtistIds) rather than
        // the tag/index entity. GetArtist(name) returns the tag entity.
        if (albums.Count == 0 && !string.IsNullOrEmpty(artist.Name))
        {
            var tagEntity = _library.GetArtist(artist.Name);
            if (tagEntity != null && tagEntity.Id != guid)
            {
                _logger.LogInformation("[Subsonic] getArtist fallback: tag entity {TagId} differs from passed {Guid}, retrying", tagEntity.Id, guid);
                var fallbackQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    AlbumArtistIds = [tagEntity.Id],
                    Recursive = true,
                };
                ApplyFolderScoping(fallbackQuery, folderIds);
                albums = _library.GetItemList(fallbackQuery).OfType<MusicAlbum>().ToList();
            }
        }

        var mapped = ItemMapper.ToArtistWithAlbums(artist, albums);
        var json = SubsonicEnvelope.Ok(new() { ["artist"] = mapped });
        return Respond(format, json, XmlBuilder.Artist(mapped));
    }

    // ── getAlbum ─────────────────────────────────────────────────────────────

    private IActionResult GetAlbum(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = _library.GetItemById<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = guid,
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();

        var resolvedArtistId = ResolveArtistTagId(album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault());
        var mapped = ItemMapper.ToAlbum(album, songs, resolvedArtistId);
        var json = SubsonicEnvelope.Ok(new() { ["album"] = mapped });
        return Respond(format, json, XmlBuilder.Album(mapped));
    }

    // ── getSong ──────────────────────────────────────────────────────────────

    private IActionResult GetSong(QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var song = _library.GetItemById<Audio>(guid);
        if (song == null) return ErrorResponse(format, ErrorCode.NotFound, "Song not found");

        var mapped = ToSongWithArtist(song);
        var json = SubsonicEnvelope.Ok(new() { ["song"] = mapped });
        return Respond(format, json, XmlBuilder.Song(mapped));
    }

    // ── getMusicDirectory ────────────────────────────────────────────────────

    private IActionResult GetMusicDirectory(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var item = _library.GetItemById(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        var children = _library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = guid,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
        });

        var childMaps = children.Select(c => c switch
        {
            MusicAlbum album => ToAlbumWithArtist(album),
            Audio song => ToSongWithArtist(song),
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

        // Search the artist index (tag entities, file-tag names) rather than GetItemList(MusicArtist)
        // which returns folder/hierarchy entities with Jellyfin-normalized names (e.g. "B.I.G_" instead of "B.I.G.").
        var folderIdsForSearch = GetEffectiveFolderIds(auth, null);
        var cacheKeyForSearch = $"artistIndex:{auth.JellyfinUserId}:{(folderIdsForSearch == null ? "all" : string.Join(",", folderIdsForSearch.OrderBy(x => x)))}";
        var cachedForSearch = SubsonicStore.GetDerivedCache(cacheKeyForSearch);
        var artistIndex = cachedForSearch != null
            ? (JsonSerializer.Deserialize<List<ArtistCacheEntry>>(cachedForSearch.ValueJson) ?? [])
                .Select(a => (a.Id, a.Name, a.AlbumCount))
            : BuildArtistList(user, folderIdsForSearch).Select(a => (a.Id, a.Name, a.AlbumCount));

        var lowerQuery = query.ToLowerInvariant();
        var artists = artistIndex
            .Where(a => a.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
            .Skip(artistOffset)
            .Take(artistCount)
            .Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["name"] = a.Name, ["coverArt"] = $"ar-{a.Id}", ["albumCount"] = a.AlbumCount })
            .ToList();

        var albums = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = albumCount,
            StartIndex = albumOffset,
            Recursive = true,
        }).OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.Audio],
            Limit = songCount,
            StartIndex = songOffset,
            Recursive = true,
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

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

        if (type == "recent")
        {
            var recentFolderIds = GetEffectiveFolderIds(auth, p.MusicFolderId);

            // Jellyfin only sets LastPlayedDate on Audio (track) entities, not MusicAlbum.
            // Derive recently-played album order from track play history instead.
            var trackLimit = Math.Max(200, (offset + size) * 10);
            var trackQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = trackLimit,
                Recursive = true,
            };
            ApplyFolderScoping(trackQuery, recentFolderIds);

            var seenAlbums = new HashSet<Guid>();
            var albumIds = new List<Guid>();
            foreach (var track in _library.GetItemList(trackQuery).OfType<Audio>())
            {
                if (track.ParentId != Guid.Empty && seenAlbums.Add(track.ParentId))
                    albumIds.Add(track.ParentId);
            }
            var pageIds = albumIds.Skip(offset).Take(size).ToList();

            var recentAlbums = pageIds
                .Select(id => _library.GetItemById<MusicAlbum>(id))
                .Where(a => a != null)
                .Cast<MusicAlbum>()
                .Select(ToAlbumWithArtist)
                .ToList();

            var recentJson = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = recentAlbums } });
            return Respond(format, recentJson, XmlBuilder.AlbumList(recentAlbums, v2));
        }

        var (sortBy, sortOrder) = type switch
        {
            "newest" => (ItemSortBy.DateCreated, SortOrder.Descending),
            "alphabeticalByName" => (ItemSortBy.SortName, SortOrder.Ascending),
            "alphabeticalByArtist" => (ItemSortBy.AlbumArtist, SortOrder.Ascending),
            "random" => (ItemSortBy.Random, SortOrder.Ascending),
            "highest" => (ItemSortBy.CommunityRating, SortOrder.Descending),
            "frequent" => (ItemSortBy.PlayCount, SortOrder.Descending),
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
        ApplyFolderScoping(query, folderIds);

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();
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
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

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
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

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
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

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
            ? (pl.LinkedChildren ?? Array.Empty<LinkedChild>())
                .Select(lc => lc.ItemId.HasValue ? _library.GetItemById<Audio>(lc.ItemId.Value) : null)
                .Where(a => a != null)
                .Cast<Audio>()
                .Select(ToSongWithArtist)
                .ToList()
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
        var id = p.Id ?? p.Get("playlistId");
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var pl = _library.GetItemById<Playlist>(guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        // Rename if requested
        var name = p.Get("name");
        if (!string.IsNullOrEmpty(name))
        {
            pl.Name = name;
            await _library.UpdateItemAsync(pl, pl.GetParent(), ItemUpdateType.MetadataEdit, CancellationToken.None);
        }

        // Remove songs by index (collect entryIds first so index shifting doesn't matter)
        var indexesToRemove = Request.Query["songIndexToRemove"]
            .Select(s => int.TryParse(s, out var i) ? i : -1)
            .Where(i => i >= 0)
            .OrderByDescending(i => i)
            .ToList();

        if (indexesToRemove.Count > 0)
        {
            var children = pl.LinkedChildren ?? Array.Empty<LinkedChild>();
            var entryIds = indexesToRemove
                .Where(i => i < children.Length)
                .Select(i => children[i].ItemId?.ToString("N"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();

            if (entryIds.Count > 0)
                await _playlists.RemoveItemFromPlaylistAsync(guid.ToString("N"), entryIds);
        }

        // Add songs
        var songIdsToAdd = Request.Query["songIdToAdd"]
            .Select(s => s ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => { Guid.TryParse(ItemMapper.StripPrefix(s), out var g); return g; })
            .Where(g => g != Guid.Empty)
            .ToArray();

        if (songIdsToAdd.Length > 0)
            await _playlists.AddItemToPlaylistAsync(guid, songIdsToAdd, user.Id);

        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult DeletePlaylist(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var pl = _library.GetItemById<Playlist>(guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        _library.DeleteItem(pl, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
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
            if (data == null) continue;
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
        if (data == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
        data.Rating = rating > 0 ? rating : null;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── scrobble ─────────────────────────────────────────────────────────────

    private async Task<IActionResult> Scrobble(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var item = _library.GetItemById<Audio>(guid);
        if (item == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var submissionParam = Request.Query["submission"].ToString();
        var isSubmission = !string.Equals(submissionParam, "false", StringComparison.OrdinalIgnoreCase);

        var timeParam = Request.Query["time"].ToString();
        long? positionTicks = long.TryParse(timeParam, out var ms) ? ms * 10_000L : null;

        if (isSubmission)
        {
            var data = _userData.GetUserData(user, item);
            if (data != null)
            {
                data.PlayCount++;
                data.LastPlayedDate = DateTimeOffset.UtcNow.UtcDateTime;
                _userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
            }
        }

        try
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var clientName = Request.Query["c"].ToString() is { Length: > 0 } cn ? cn : (auth.JellyfinDeviceName ?? "Subsonic");
            var clientVersion = Request.Query["v"].ToString() is { Length: > 0 } cv ? cv : "1.0.0";
            var session = await _sessions.LogSessionActivity(
                clientName, clientVersion,
                auth.JellyfinDeviceId ?? "subfin-unknown",
                auth.JellyfinDeviceName ?? "Subsonic Device",
                remoteIp, user);

            await _sessions.OnPlaybackStart(new PlaybackStartInfo
            {
                ItemId = guid,
                SessionId = session.Id,
                PositionTicks = 0L,
                PlayMethod = PlayMethod.DirectPlay,
                IsPaused = false,
                CanSeek = true
            });

            if (isSubmission)
            {
                await _sessions.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = guid,
                    SessionId = session.Id,
                    PositionTicks = positionTicks ?? 0L,
                    IsPaused = false
                });
                await _sessions.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = guid,
                    SessionId = session.Id,
                    PositionTicks = positionTicks ?? 0L,
                    Failed = false
                });
                _logger.LogInformation("[Subsonic] scrobble: sent start+progress+stop (submission)");
            }
            else
            {
                _ = _sessions.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = guid,
                    SessionId = session.Id,
                    PositionTicks = positionTicks ?? 0L,
                    IsPaused = false
                });
                _logger.LogInformation("[Subsonic] scrobble: sent start+progress (now playing)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Subsonic] scrobble: session reporting failed (non-fatal)");
        }

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
                ["mediaType"] = "song",
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
            return audio != null ? ToSongWithArtist(audio) : null;
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

        // Expand IDs to a flat list of audio track GUIDs.
        // Artist IDs are bare GUIDs; album IDs use al- prefix; playlist IDs use pl- prefix.
        var seen = new HashSet<string>();
        var flatIds = new List<string>();
        foreach (var id in ids)
        {
            if (id.StartsWith("al-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.Substring(3), out var albumGuid)) continue;
                var tracks = _library.GetItemList(new InternalItemsQuery(user)
                {
                    ParentId = albumGuid,
                    IncludeItemTypes = [BaseItemKind.Audio],
                }).OfType<Audio>().ToList();
                foreach (var t in tracks)
                    if (seen.Add(t.Id.ToString("N"))) flatIds.Add(t.Id.ToString("N"));
            }
            else if (id.StartsWith("pl-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.Substring(3), out var plGuid)) continue;
                var pl = _library.GetItemById<Playlist>(plGuid);
                if (pl == null) continue;
                foreach (var lc in pl.LinkedChildren ?? Array.Empty<LinkedChild>())
                {
                    if (!lc.ItemId.HasValue) continue;
                    var tId = lc.ItemId.Value.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
            else
            {
                // Bare GUID — resolve item type to handle artist, album, or track
                if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) continue;
                var item = _library.GetItemById(guid);
                if (item is MusicArtist artistItem)
                {
                    var albums = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        AlbumArtistIds = [artistItem.Id],
                        Recursive = true
                    }).OfType<MusicAlbum>().ToList();
                    foreach (var album in albums)
                    {
                        var tracks = _library.GetItemList(new InternalItemsQuery(user)
                        {
                            ParentId = album.Id,
                            IncludeItemTypes = [BaseItemKind.Audio],
                        }).OfType<Audio>().ToList();
                        foreach (var t in tracks)
                            if (seen.Add(t.Id.ToString("N"))) flatIds.Add(t.Id.ToString("N"));
                    }
                }
                else if (item is MusicAlbum albumItem)
                {
                    var tracks = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        ParentId = albumItem.Id,
                        IncludeItemTypes = [BaseItemKind.Audio],
                    }).OfType<Audio>().ToList();
                    foreach (var t in tracks)
                        if (seen.Add(t.Id.ToString("N"))) flatIds.Add(t.Id.ToString("N"));
                }
                else
                {
                    var tId = guid.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var uid = SubsonicStore.InsertShare(device.Id, ids, flatIds.Count > 0 ? flatIds : ids, desc, expiresAt, secret);

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
        var createdDt = DateTime.Parse(s.CreatedAt, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var created = createdDt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var expires = s.ExpiresAt != null
            ? ToShareDateTime(s.ExpiresAt)
            : createdDt.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var songs = s.EntryIdsFlat.Select(id =>
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return null;
            var audio = _library.GetItemById<Audio>(guid);
            return audio != null ? ToSongWithArtist(audio) : null;
        }).Where(x => x != null).Cast<Dictionary<string, object?>>().ToList();

        return new ShareXml(s.ShareUid, url, s.Description, user.Username, created, expires, s.VisitCount, songs);
    }

    private static string ToShareDateTime(string sqliteOrIsoDateTime) =>
        DateTime.Parse(sqliteOrIsoDateTime, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

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
            .OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();
        var songs = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Audio], IsFavorite = true, Recursive = true })
            .OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            [v2 ? "starred2" : "starred"] = new Dictionary<string, object> { ["artist"] = artists, ["album"] = albums, ["song"] = songs }
        });
        return Respond(format, json, XmlBuilder.Starred(artists, albums, songs, v2));
    }

    // ── getUser / getUsers ───────────────────────────────────────────────────

    private IActionResult GetUser(User user, string format)
    {
        var userDict = new Dictionary<string, object>
        {
            ["username"] = user.Username,
            ["email"] = "",
            ["scrobblingEnabled"] = false,
            ["adminRole"] = false,
            ["settingsRole"] = false,
            ["downloadRole"] = true,
            ["uploadRole"] = false,
            ["playlistRole"] = true,
            ["coverArtRole"] = false,
            ["commentRole"] = false,
            ["podcastRole"] = false,
            ["streamRole"] = true,
            ["jukeboxRole"] = false,
            ["shareRole"] = false,
            ["videoConversionRole"] = false,
        };
        var json = SubsonicEnvelope.Ok(new() { ["user"] = userDict });
        return Respond(format, json, XmlBuilder.User(user.Username));
    }

    // ── getScanStatus ────────────────────────────────────────────────────────

    private IActionResult GetScanStatus(string format)
    {
        var json = SubsonicEnvelope.Ok(new()
        { ["scanStatus"] = new Dictionary<string, object> { ["scanning"] = false, ["count"] = 0 } });
        return Respond(format, json, XmlBuilder.ScanStatus());
    }

    // ── getArtistInfo / getArtistInfo2 ───────────────────────────────────────

    private async Task<IActionResult> GetArtistInfo(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = _library.GetItemById<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var mbid = artist.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzArtist);
        var info = await FetchLastFmArtistInfo(mbid, artist.Name);

        var similarArtistDicts = new List<Dictionary<string, object?>>();
        if (info != null)
        {
            foreach (var name in info.SimilarNames)
            {
                var similar = _library.GetArtist(name);
                if (similar == null) continue;
                similarArtistDicts.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"ar-{similar.Id:N}",
                    ["name"] = similar.Name ?? name,
                    ["albumCount"] = 0,
                });
            }
        }

        var bio = info?.Bio;
        var mbidResult = info?.Mbid ?? mbid;
        var url = info?.Url;

        // Build the artist image URL pointing to Jellyfin's image endpoint.
        // Jellyfin stores artist images on the scanned entity; this URL serves it directly.
        var artistImageUrl = $"{Request.Scheme}://{Request.Host}/Items/{artist.Id:N}/Images/Primary";

        var jsonKey = v2 ? "artistInfo2" : "artistInfo";
        var jsonInfo = new Dictionary<string, object>
        {
            ["smallImageUrl"] = artistImageUrl,
            ["mediumImageUrl"] = artistImageUrl,
            ["largeImageUrl"] = artistImageUrl,
            ["similarArtist"] = similarArtistDicts.Select(s => (object)s).ToList(),
        };
        if (bio != null) jsonInfo["biography"] = bio;
        if (mbidResult != null) jsonInfo["musicBrainzId"] = mbidResult;
        if (url != null) jsonInfo["lastFmUrl"] = url;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, XmlBuilder.ArtistInfo(bio, mbidResult, url, artistImageUrl, similarArtistDicts, v2));
    }

    // ── getAlbumInfo / getAlbumInfo2 ─────────────────────────────────────────

    private async Task<IActionResult> GetAlbumInfo(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = _library.GetItemById<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var mbid = album.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzAlbum);
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault();
        var info = await FetchLastFmAlbumInfo(artistName, album.Name, mbid);

        var notes = info?.Notes;
        var mbidResult = info?.Mbid ?? mbid;
        var url = info?.Url;

        var jsonKey = v2 ? "albumInfo2" : "albumInfo";
        var jsonInfo = new Dictionary<string, object>();
        if (notes != null) jsonInfo["notes"] = notes;
        if (mbidResult != null) jsonInfo["musicBrainzId"] = mbidResult;
        if (url != null) jsonInfo["lastFmUrl"] = url;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, XmlBuilder.AlbumInfo(notes, mbidResult, url, v2));
    }

    // ── getSimilarSongs / getSimilarSongs2 ───────────────────────────────────

    private IActionResult GetSimilarSongs(User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var count = p.GetInt("count", 50);
        var dtoOptions = new DtoOptions();
        IReadOnlyList<BaseItem> results;

        var item = _library.GetItemById(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Item not found");
        if (item is MusicArtist ma)
            results = _musicManager.GetInstantMixFromArtist(ma, user, dtoOptions);
        else
            results = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);

        var songs = results.Take(count).OfType<Audio>().Select(ToSongWithArtist).ToList();
        var jsonKey = v2 ? "similarSongs2" : "similarSongs";
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.SimilarSongs(songs, v2));
    }

    // ── getTopSongs ──────────────────────────────────────────────────────────

    private IActionResult GetTopSongs(User user, QueryParams p, string format)
    {
        var artistName = p.Get("artist");
        if (string.IsNullOrEmpty(artistName))
            return Respond(format, SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } }), XmlBuilder.TopSongs([]));

        var count = p.GetInt("count", 50);
        var tagArtist = _library.GetArtist(artistName);
        if (tagArtist == null)
            return Respond(format, SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } }), XmlBuilder.TopSongs([]));

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            AlbumArtistIds = [tagArtist.Id],
            OrderBy = [(ItemSortBy.PlayCount, SortOrder.Descending)],
            Limit = count,
            Recursive = true,
        }).OfType<Audio>().Select(a => ItemMapper.ToSong(a, artistId: $"ar-{tagArtist.Id:N}")).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.TopSongs(songs));
    }

    // ── Last.fm helpers ──────────────────────────────────────────────────────

    private record LastFmArtistInfo(string? Bio, string? Url, string? Mbid, List<string> SimilarNames);
    private record LastFmAlbumInfo(string? Notes, string? Url, string? Mbid);

    private async Task<LastFmArtistInfo?> FetchLastFmArtistInfo(string? mbid, string? artistName)
    {
        var apiKey = SubsonicPlugin.Instance?.Configuration?.LastFmApiKey;
        if (string.IsNullOrEmpty(apiKey)) return null;

        var cacheKey = !string.IsNullOrEmpty(mbid)
            ? $"lastfm:artist:mbid:{mbid}"
            : $"lastfm:artist:name:{(artistName ?? "").ToLowerInvariant()}";

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null && (DateTime.UtcNow - DateTime.Parse(cached.CachedAt)).TotalDays < 30)
        {
            try { return JsonSerializer.Deserialize<LastFmArtistInfo>(cached.ValueJson); } catch { }
        }

        try
        {
            var url = !string.IsNullOrEmpty(mbid)
                ? $"https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&mbid={Uri.EscapeDataString(mbid)}&api_key={apiKey}&format=json"
                : $"https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&artist={Uri.EscapeDataString(artistName ?? "")}&api_key={apiKey}&format=json";

            var client = _httpClientFactory.CreateClient();
            var resp = await client.GetStringAsync(url);
            var doc = JsonNode.Parse(resp);
            var a = doc?["artist"];
            if (a == null) return null;

            var bio = a["bio"]?["summary"]?.GetValue<string>();
            var lastFmUrl = a["url"]?.GetValue<string>();
            var artistMbid = a["mbid"]?.GetValue<string>();
            var similar = a["similar"]?["artist"]?.AsArray()
                .Select(n => n?["name"]?.GetValue<string>()).Where(n => n != null).Cast<string>().ToList() ?? [];

            var info = new LastFmArtistInfo(bio, lastFmUrl, artistMbid, similar);
            SubsonicStore.SetDerivedCache(cacheKey, JsonSerializer.Serialize(info), null);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Subsonic] Last.fm artist lookup failed for {Name}", artistName);
            return null;
        }
    }

    private async Task<LastFmAlbumInfo?> FetchLastFmAlbumInfo(string? artistName, string? albumName, string? mbid)
    {
        var apiKey = SubsonicPlugin.Instance?.Configuration?.LastFmApiKey;
        if (string.IsNullOrEmpty(apiKey)) return null;

        var cacheKey = !string.IsNullOrEmpty(mbid)
            ? $"lastfm:album:mbid:{mbid}"
            : $"lastfm:album:{(artistName ?? "").ToLowerInvariant()}:{(albumName ?? "").ToLowerInvariant()}";

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null && (DateTime.UtcNow - DateTime.Parse(cached.CachedAt)).TotalDays < 30)
        {
            try { return JsonSerializer.Deserialize<LastFmAlbumInfo>(cached.ValueJson); } catch { }
        }

        try
        {
            var url = !string.IsNullOrEmpty(mbid)
                ? $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&mbid={Uri.EscapeDataString(mbid)}&api_key={apiKey}&format=json"
                : $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&artist={Uri.EscapeDataString(artistName ?? "")}&album={Uri.EscapeDataString(albumName ?? "")}&api_key={apiKey}&format=json";

            var client = _httpClientFactory.CreateClient();
            var resp = await client.GetStringAsync(url);
            var doc = JsonNode.Parse(resp);
            var al = doc?["album"];
            if (al == null) return null;

            var notes = al["wiki"]?["summary"]?.GetValue<string>();
            var lastFmUrl = al["url"]?.GetValue<string>();
            var albumMbid = al["mbid"]?.GetValue<string>();

            var info = new LastFmAlbumInfo(notes, lastFmUrl, albumMbid);
            SubsonicStore.SetDerivedCache(cacheKey, JsonSerializer.Serialize(info), null);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Subsonic] Last.fm album lookup failed for {Name}", albumName);
            return null;
        }
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

    private const string PluginApiKeyName = "Subsonic-Plugin-Internal";

    private async Task<string?> GetOrCreatePluginApiKey()
    {
        const string cacheKey = "plugin-api-key";
        const double CacheDays = 30;

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null)
        {
            var ageDays = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(cached.CachedAt)).TotalDays;
            if (ageDays < CacheDays)
                return cached.ValueJson;
        }

        var keys = await _authManager.GetApiKeys();
        var existing = keys.FirstOrDefault(k => k.AppName == PluginApiKeyName);

        if (existing == null)
        {
            await _authManager.CreateApiKey(PluginApiKeyName);
            keys = await _authManager.GetApiKeys();
            existing = keys.FirstOrDefault(k => k.AppName == PluginApiKeyName);
        }

        if (existing?.AccessToken == null) return null;

        SubsonicStore.SetDerivedCache(cacheKey, existing.AccessToken, null);
        return existing.AccessToken;
    }

    private static (string container, string audioCodec, string mimeType) MapTranscodeFormat(string? format) =>
        (format ?? "mp3") switch
        {
            "mp3"  => ("mp3",  "mp3",    "audio/mpeg"),
            "aac"  => ("aac",  "aac",    "audio/aac"),
            "ogg"  => ("ogg",  "vorbis", "audio/ogg"),
            "opus" => ("webm", "opus",   "audio/webm"),
            "flac" => ("flac", "flac",   "audio/flac"),
            _      => ("mp3",  "mp3",    "audio/mpeg"),
        };

    private async Task<IActionResult> Stream(AuthResult auth, QueryParams p)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return BadRequest();
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return NotFound();

        // Check share allowlist
        if (auth.ShareAllowedIds != null && !auth.ShareAllowedIds.Contains(guid.ToString("N")))
            return StatusCode(403);

        var item = _library.GetItemById<Audio>(guid);
        if (item?.Path == null) return NotFound();

        var format  = p.Format;
        var bitRate = p.MaxBitRate;   // kbps; 0 = unspecified
        var timeOff = p.TimeOffset;   // seconds; 0 = from start

        bool needsTranscode = (format != null && format != "raw") || bitRate > 0 || timeOff > 0;

        _logger.LogInformation("[Subsonic] stream id={Id} format={Format} bitRate={BitRate} timeOff={TimeOff} needsTranscode={NeedsTranscode}",
            id, format, bitRate, timeOff, needsTranscode);

        if (!needsTranscode)
            return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), null, true);

        var apiKey = await GetOrCreatePluginApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("[Subsonic] Could not obtain plugin API key — serving direct");
            return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), null, true);
        }

        var (container, audioCodec, mimeType) = MapTranscodeFormat(format);
        var qs = $"audioCodec={audioCodec}&static=false" +
                 $"&userId={auth.JellyfinUserId}" +
                 $"&deviceId={Uri.EscapeDataString(auth.JellyfinDeviceId ?? "subsonic-plugin")}";
        if (bitRate > 0) qs += $"&audioBitRate={bitRate * 1000}";
        if (timeOff > 0) qs += $"&startTimeTicks={(long)timeOff * 10_000_000L}";

        var url = $"{Request.Scheme}://{Request.Host}/Audio/{guid:N}/stream.{container}?{qs}";
        _logger.LogInformation("[Subsonic] stream proxy → {Url}", url);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

        var rangeHeader = Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
            req.Headers.TryAddWithoutValidation("Range", rangeHeader);

        var resp = await _httpClientFactory.CreateClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Response.RegisterForDispose(resp);
        Response.StatusCode = (int)resp.StatusCode;

        if (resp.Content.Headers.ContentLength.HasValue)
            Response.Headers["Content-Length"] = resp.Content.Headers.ContentLength.Value.ToString();
        if (resp.Content.Headers.ContentRange != null)
            Response.Headers["Content-Range"] = resp.Content.Headers.ContentRange.ToString();

        // Use our known mimeType — Jellyfin may return "video/webm" for audio-only WebM which confuses clients
        return new FileStreamResult(await resp.Content.ReadAsStreamAsync(), mimeType);
    }

    private IActionResult Download(QueryParams p)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return BadRequest();
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return NotFound();

        var item = _library.GetItemById<Audio>(guid);
        if (item?.Path == null) return NotFound();

        return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), Path.GetFileName(item.Path), true);
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

    // Converts a Jellyfin music folder GUID string to a stable positive integer for JSON responses.
    // subsonic-kotlin (Navic) types MusicFolder.id as Int, not String.
    private static int FolderIdToInt(string guidStr) =>
        (int)(BitConverter.ToUInt32(Guid.Parse(guidStr).ToByteArray(), 0) & 0x7FFFFFFF);

    // Returns false and sets error if id is null/empty or fails Guid parse; sets guid on success.
    private bool TryParseItemId(string? id, string format, out Guid guid, out IActionResult? error)
    {
        if (string.IsNullOrEmpty(id))
        {
            guid = default;
            error = ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
            return false;
        }
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out guid))
        {
            error = ErrorResponse(format, ErrorCode.NotFound, "Not found");
            return false;
        }
        error = null;
        return true;
    }

    private static void ApplyFolderScoping(InternalItemsQuery query, List<string>? folderIds)
    {
        if (folderIds != null)
            query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();
    }

    private Dictionary<string, object?> ToSongWithArtist(Audio s) =>
        ItemMapper.ToSong(s, artistId: ResolveArtistTagId(s.AlbumArtists.FirstOrDefault() ?? s.Artists.FirstOrDefault()));

    private Dictionary<string, object?> ToAlbumWithArtist(MusicAlbum a) =>
        ItemMapper.ToAlbumShort(a, ResolveArtistTagId(a.AlbumArtist ?? a.AlbumArtists.FirstOrDefault()));

    private List<string>? GetEffectiveFolderIds(AuthResult auth, string? clientParam)
    {
        if (!string.IsNullOrEmpty(clientParam))
        {
            // Clients like Navic send musicFolderId as an integer (matching the int id we emit
            // in JSON). Map it back to the GUID string used internally.
            if (int.TryParse(clientParam, out var intId))
            {
                var match = _library.GetVirtualFolders()
                    .Where(f => f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
                    .FirstOrDefault(f => FolderIdToInt(f.ItemId) == intId);
                return match != null ? [match.ItemId] : null;
            }
            return [clientParam];
        }
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
    public string? Format => Get("format");
    public int MaxBitRate => GetInt("maxBitRate", 0) > 0 ? GetInt("maxBitRate", 0) : GetInt("bitRate", 0);
    public int TimeOffset => GetInt("timeOffset", 0);
    public string? Get(string key) { var v = _q[key].ToString(); return string.IsNullOrEmpty(v) ? null : v; }
    public int GetInt(string key, int def) => int.TryParse(_q[key], out var v) ? v : def;
    public long GetLong(string key, long def) => long.TryParse(_q[key], out var v) ? v : def;
}
