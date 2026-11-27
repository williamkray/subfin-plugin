using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Subsonic.Mappers;

/// <summary>
/// Library query helpers shared between the Subsonic REST controller and the
/// /subfin web UI controller, so both expand share items and resolve artists
/// identically (special-character-safe tag entity IDs).
/// </summary>
public static class LibraryQueries
{
    /// <summary>
    /// Expands a list of Subsonic IDs (artist / al- album / pl- playlist / bare track)
    /// into a de-duplicated flat list of audio track GUIDs (no-dash "N" format).
    /// </summary>
    public static List<string> ExpandShareIds(ILibraryManager library, User user, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>();
        var flatIds = new List<string>();
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;

            if (id.StartsWith("al-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.Substring(3), out var albumGuid)) continue;
                if (library.GetItemById<MusicAlbum>(albumGuid, user) == null) continue;
                AddAlbumTracks(library, user, albumGuid, seen, flatIds);
            }
            else if (id.StartsWith("pl-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.Substring(3), out var plGuid)) continue;
                var pl = library.GetItemById<Playlist>(plGuid, user);
                if (pl == null) continue;
                foreach (var lc in pl.LinkedChildren ?? Array.Empty<LinkedChild>())
                {
                    if (!lc.ItemId.HasValue) continue;
                    if (library.GetItemById<Audio>(lc.ItemId.Value, user) == null) continue;
                    var tId = lc.ItemId.Value.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
            else
            {
                // Bare GUID (or ar-/al- prefixed) — resolve item type to handle artist, album, or track
                if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) continue;
                // Visibility-checked: ids the user can't access (or that don't exist) are dropped
                var item = library.GetItemById<BaseItem>(guid, user);
                if (item == null) continue;
                if (item is MusicArtist artistItem)
                {
                    var albums = library.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        AlbumArtistIds = [artistItem.Id],
                        Recursive = true
                    }).OfType<MusicAlbum>().ToList();
                    foreach (var album in albums)
                        AddAlbumTracks(library, user, album.Id, seen, flatIds);
                }
                else if (item is MusicAlbum albumItem)
                {
                    AddAlbumTracks(library, user, albumItem.Id, seen, flatIds);
                }
                else if (item is Audio)
                {
                    var tId = guid.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
        }
        return flatIds;
    }

    private static void AddAlbumTracks(ILibraryManager library, User user, Guid albumGuid, HashSet<string> seen, List<string> flatIds)
    {
        var tracks = library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = albumGuid,
            Recursive = true,   // multi-disc albums: tracks live in disc subfolders
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending), (ItemSortBy.SortName, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();
        foreach (var t in tracks)
            if (seen.Add(t.Id.ToString("N"))) flatIds.Add(t.Id.ToString("N"));
    }

    /// <summary>
    /// Builds the artist index (tag entity IDs + display names + album counts) from
    /// albums in scope. Uses ILibraryManager.GetArtist(name) so returned IDs match what
    /// AlbumArtistIds queries expect — see the "Jellyfin artist entity model" notes.
    /// </summary>
    public static List<(string Id, string Name, int AlbumCount)> BuildArtistList(ILibraryManager library, User user, List<string>? folderIds)
    {
        var albumQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
        };
        if (folderIds != null)
            albumQuery.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var allAlbums = library.GetItemList(albumQuery).OfType<MusicAlbum>();

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

            var artistEntity = library.GetArtist(artistName);
            if (artistEntity == null) continue;

            byKey[key] = (artistEntity.Id.ToString("N"), artistName, 1);
        }
        return byKey.Values.Select(v => (v.Id, v.Name, v.Count)).OrderBy(v => v.Name).ToList();
    }

    private static readonly Regex _artistKeyRegex = new(@"[\s._/*'""\-]+", RegexOptions.Compiled);
    private static string CanonicalArtistKey(string name) => _artistKeyRegex.Replace(name.ToLowerInvariant(), "");
}
