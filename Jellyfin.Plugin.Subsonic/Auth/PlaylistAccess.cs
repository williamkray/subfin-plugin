using System;
using System.Linq;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>
/// Playlist write rules, mirroring Jellyfin's own model: the owner has full control; users
/// listed in <see cref="Playlist.Shares"/> with <c>CanEdit</c> may edit; only the owner may
/// delete. Read access is NOT decided here — use <c>ILibraryManager.GetItemById&lt;Playlist&gt;(id, user)</c>,
/// which applies Jellyfin's own visibility (owner / OpenAccess / Shares for playlists-folder
/// playlists, ordinary library access for .m3u playlists inside a music library).
/// </summary>
public static class PlaylistAccess
{
    public static bool CanEdit(Playlist pl, Guid userId) =>
        IsOwner(pl, userId)
        || (pl.Shares?.Any(s => s.UserId.Equals(userId) && s.CanEdit) ?? false);

    public static bool CanDelete(Playlist pl, Guid userId) => IsOwner(pl, userId);

    private static bool IsOwner(Playlist pl, Guid userId) =>
        !userId.Equals(Guid.Empty) && pl.OwnerUserId.Equals(userId);
}
