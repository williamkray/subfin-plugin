using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Subsonic.Mappers;

/// <summary>Maps Jellyfin entities to OpenSubsonic response shapes (Dictionary for flexible JSON/XML).</summary>
public static class ItemMapper
{
    private const string IgnoredArticles = "The An A Die Das Ein Eine Les Le La";
    private const long TicksPerSecond = 10_000_000L;

    public static int TicksToSeconds(long? ticks) =>
        ticks.HasValue ? (int)(ticks.Value / TicksPerSecond) : 0;

    public static string AudioMimeType(string? container) => container?.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "ogg" or "oga" => "audio/ogg",
        "opus" => "audio/ogg; codecs=opus",
        "aac" or "m4a" => "audio/aac",
        "wav" => "audio/wav",
        "wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };

    public static string IndexLetter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "#";
        var first = char.ToUpperInvariant(name.TrimStart()[0]);
        return char.IsAsciiLetterOrDigit(first) ? first.ToString() : "#";
    }

    public static string StripPrefix(string id) =>
        id.StartsWith("ar-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id.StartsWith("al-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id.StartsWith("pl-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id;

    // ── Artist ───────────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToArtist(MusicArtist artist, int albumCount = 0) => new()
    {
        ["id"] = artist.Id.ToString("N"),
        ["name"] = artist.Name ?? "",
        ["coverArt"] = $"ar-{artist.Id:N}",
        ["albumCount"] = albumCount,
    };

    public static Dictionary<string, object?> ToArtistWithAlbums(MusicArtist artist, IEnumerable<MusicAlbum> albums)
    {
        var albumList = albums.ToList();
        var artistId = $"ar-{artist.Id:N}";
        return new()
        {
            ["id"] = artist.Id.ToString("N"),
            ["name"] = artist.Name ?? "",
            ["coverArt"] = $"ar-{artist.Id:N}",
            ["albumCount"] = albumList.Count,
            ["album"] = albumList.Select(a => ToAlbumShort(a, artistId)).ToList(),
        };
    }

    // ── Album ────────────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToAlbumShort(MusicAlbum album, string? resolvedArtistId = null)
    {
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
        return new()
        {
            ["id"] = album.Id.ToString("N"),
            ["name"] = album.Name ?? "",
            ["isDir"] = true,
            ["coverArt"] = $"al-{album.Id:N}",
            ["songCount"] = album.Tracks.Count(),
            ["duration"] = TicksToSeconds(album.RunTimeTicks),
            ["playCount"] = 0,
            ["artist"] = artistName,
            ["artistId"] = resolvedArtistId ?? "",
            ["year"] = album.ProductionYear,
            ["genre"] = album.Genres.FirstOrDefault() ?? "",
            ["created"] = (album.DateCreated == default ? DateTimeOffset.UnixEpoch.UtcDateTime : album.DateCreated).ToString("o"),
        };
    }

    public static Dictionary<string, object?> ToAlbum(MusicAlbum album, IEnumerable<Audio> songs, string? resolvedArtistId = null)
    {
        var songList = songs.ToList();
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
        return new()
        {
            ["id"] = album.Id.ToString("N"),
            ["parent"] = album.ParentId.ToString("N"),
            ["album"] = album.Name ?? "",
            ["title"] = album.Name ?? "",
            ["name"] = album.Name ?? "",
            ["isDir"] = true,
            ["coverArt"] = $"al-{album.Id:N}",
            ["songCount"] = songList.Count,
            ["created"] = (album.DateCreated == default ? DateTimeOffset.UnixEpoch.UtcDateTime : album.DateCreated).ToString("o"),
            ["duration"] = songList.Sum(s => TicksToSeconds(s.RunTimeTicks)),
            ["playCount"] = 0,
            ["artistId"] = resolvedArtistId ?? "",
            ["artist"] = artistName,
            ["year"] = album.ProductionYear,
            ["genre"] = album.Genres.FirstOrDefault() ?? "",
            ["song"] = songList.Select(s => ToSong(s, album.Id.ToString("N"), album.Name, artistName, resolvedArtistId)).ToList(),
        };
    }

    // ── Song ─────────────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToSong(Audio song, string? albumId = null, string? albumName = null, string? artistName = null, string? artistId = null)
    {
        var duration = TicksToSeconds(song.RunTimeTicks);
        var size = song.Size ?? 0L;
        var bitRate = duration > 0 && size > 0 ? (int)((size * 8L) / duration / 1000L) : 0;

        // Audio.ParentId is the album's Guid
        var effectiveAlbumId = albumId ?? song.ParentId.ToString("N");
        var primaryArtist = artistName ?? song.AlbumArtists.FirstOrDefault() ?? song.Artists.FirstOrDefault() ?? "";

        // Get audio stream metadata
        var mediaStream = song.GetMediaStreams()
            .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);

        var suffix = song.Container?.ToLowerInvariant() ?? "mp3";
        var mimeType = AudioMimeType(song.Container);
        return new()
        {
            ["id"] = song.Id.ToString("N"),
            ["parent"] = effectiveAlbumId,
            ["title"] = song.Name ?? "",
            ["isDir"] = false,
            ["isVideo"] = false,
            ["type"] = "music",
            ["mediaType"] = "song",
            ["albumId"] = effectiveAlbumId,
            ["album"] = albumName ?? song.Album ?? "",
            ["artist"] = primaryArtist,
            ["artistId"] = artistId ?? "",
            ["displayArtist"] = primaryArtist,
            ["displayAlbumArtist"] = primaryArtist,
            ["coverArt"] = $"al-{effectiveAlbumId}",
            ["duration"] = duration,
            ["bitRate"] = bitRate,
            ["track"] = song.IndexNumber ?? 0,
            ["year"] = song.ProductionYear,
            ["genre"] = song.Genres.FirstOrDefault() ?? "",
            ["size"] = size,
            ["suffix"] = suffix,
            ["contentType"] = mimeType,
            ["transcodedSuffix"] = suffix,
            ["transcodedContentType"] = mimeType,
            ["discNumber"] = song.ParentIndexNumber ?? 1,
            ["path"] = song.Path ?? "",
            ["bitDepth"] = mediaStream?.BitDepth ?? 16,
            ["samplingRate"] = mediaStream?.SampleRate ?? 44100,
            ["channelCount"] = mediaStream?.Channels ?? 2,
        };
    }

    // ── Artist index ─────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToArtistsIndex(IEnumerable<(string Id, string Name, int AlbumCount)> artists)
    {
        var byLetter = new SortedDictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (id, name, albumCount) in artists)
        {
            var letter = IndexLetter(name);
            if (!byLetter.ContainsKey(letter)) byLetter[letter] = [];
            byLetter[letter].Add(new() { ["id"] = id, ["name"] = name, ["coverArt"] = $"ar-{id}", ["albumCount"] = albumCount });
        }
        return new()
        {
            ["ignoredArticles"] = IgnoredArticles,
            ["index"] = byLetter.Select(kv => new Dictionary<string, object?> { ["name"] = kv.Key, ["artist"] = kv.Value }).ToList(),
        };
    }
}
