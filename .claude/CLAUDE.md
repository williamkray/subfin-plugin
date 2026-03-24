# CLAUDE.md — subfin-plugin

Jellyfin native Subsonic plugin (C#/.NET 9, Jellyfin 10.11.6). Ported from the Node.js `subfin` project.

## What this is

An OpenSubsonic-compatible REST API exposed as a Jellyfin plugin. Subsonic/Navidrome clients (DSub2000, Substreamer, etc.) connect to `/rest/*` on the main Jellyfin port. No separate process, no HTTP calls to Jellyfin — uses Jellyfin's DI services directly.

## Commands

```bash
dotnet build                        # must pass before any commit
dotnet test                         # run xUnit tests
./scripts/deploy-dev.sh             # build + deploy to localenv Jellyfin + smoke test
```

## Localenv

Jellyfin runs in `../subfin/localenv/` (docker compose). The plugin dir is:
`../subfin/localenv/jellyfin-data/config/plugins/Subsonic_0.1.0.0/`

**meta.json footgun:** when Jellyfin fails to load a plugin it overwrites meta.json with
`"status": "NotSupported"` and `"assemblies": []`, silently skipping it on future restarts.
`deploy-dev.sh` always resets meta.json to the known-good state.

## Architecture

### Request flow
```
Subsonic client → /rest/{method} → SubsonicController → SubsonicAuth.Resolve()
                                                               ↓
                                                    ILibraryManager / IUserManager
                                                    (direct Jellyfin DI calls)
                                                               ↓
                                                          ItemMapper
                                                               ↓
                                                    XmlBuilder or JSON serializer
                                                               ↓
                                                          HTTP response
```

### Key source files

| File | Role |
|------|------|
| `Plugin.cs` | Plugin entry point; auto-generates salt; initializes SQLite store |
| `PluginServiceRegistrator.cs` | Registers SubsonicAuth via DI |
| `Configuration/PluginConfiguration.cs` | Salt, LastFM key, logRest, CORS origins |
| `Controllers/SubsonicController.cs` | All `/rest/*` endpoints |
| `Controllers/WebController.cs` | `/Subsonic/*` web UI: device management, library selection, share pages |
| `Store/SubsonicStore.cs` | All SQLite access (static class) |
| `Store/Crypto.cs` | AES-256-GCM encrypt/decrypt; PBKDF2-SHA256 key derivation |
| `Store/Schema.sql` | DB schema (embedded resource) |
| `Auth/SubsonicAuth.cs` | Resolves u/p, u/t/s, apiKey, share auth → AuthResult |
| `Mappers/ItemMapper.cs` | Maps Jellyfin entities → OpenSubsonic response shapes |
| `Response/SubsonicResponse.cs` | Constants, SubsonicEnvelope.Ok/Error |
| `Response/XmlBuilder.cs` | Hand-crafted attribute-style XML builders per endpoint |

### URL paths
- `/rest/{method}` — All Subsonic API endpoints
- `/Subsonic/` — Web UI (device management)
- `/Subsonic/share/{uid}` — Public share pages
- `/Subsonic/api/*` — Web UI REST API

### Jellyfin services used (via DI)
- `ILibraryManager` — item queries, folder listing, playlists, delete
- `IUserManager` — user lookup, credential validation
- `IUserDataManager` — favorites, ratings, play counts (SaveUserData is **synchronous**)
- `ISessionManager` — now playing sessions
- `IPlaylistManager` — CRUD playlists, GetPlaylists(userId)

## Critical Jellyfin 10.11.6 API patterns

> **Target version:** `net9.0`, `Jellyfin.Controller/Model 10.11.6`, `Jellyfin.Database.Implementations 10.11.6`

### Breaking changes from 10.9.x → 10.11.x

| What | Old (10.9.x) | New (10.11.x) |
|------|-------------|--------------|
| `User` entity | `Jellyfin.Data.Entities.User` | `Jellyfin.Database.Implementations.Entities.User` |
| `SortOrder` enum | `Jellyfin.Data.Enums.SortOrder` | `Jellyfin.Database.Implementations.Enums.SortOrder` |
| `IUserManager.AuthenticateUser` | 5 args (username, password, passwordMd5, endpoint, isSession) | 4 args (username, password, endpoint, isSession) |

Required usings (10.11.x):
```csharp
using Jellyfin.Data.Enums;                          // ItemSortBy, BaseItemKind
using Jellyfin.Database.Implementations.Entities;   // User
using Jellyfin.Database.Implementations.Enums;      // SortOrder
```

### Namespaces
- `BaseItemKind`, `ItemSortBy` → `Jellyfin.Data.Enums`
- `SortOrder` → `Jellyfin.Database.Implementations.Enums`
- `User` → `Jellyfin.Database.Implementations.Entities`
- `CollectionTypeOptions` (e.g. `.music`) → `MediaBrowser.Model.Entities`
- `InternalItemsQuery` → `MediaBrowser.Controller.Entities` (not `MediaBrowser.Model.Querying`)

### InternalItemsQuery
```csharp
var query = new InternalItemsQuery(user)   // pass User to constructor
{
    IncludeItemTypes = [BaseItemKind.MusicAlbum],
    OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
    Recursive = true,
    Limit = 20,
    StartIndex = 0,
    AncestorIds = folderGuids,             // NOT ParentIds (doesn't exist)
    AlbumArtistIds = [artistGuid],
    IsFavorite = true,
    Years = new[] { 2020, 2021, 2022 },    // NOT MinYear/MaxYear
    Genres = new List<string> { "Rock" },  // IReadOnlyList<string>
    SearchTerm = "query",
};
var items = _library.GetItemList(query);   // returns List<BaseItem>
```

### GetVirtualFolders (music folders)
```csharp
var musicFolders = _library.GetVirtualFolders()
    .Where(f => f.CollectionType == CollectionTypeOptions.music)
    .Select(f => (f.ItemId, f.Name ?? ""))  // ItemId is string, Name is string
    .ToList();
```

### Entity shapes
- `Audio.AlbumArtists` → `IReadOnlyList<string>` (strings, not objects)
- `Audio.Artists` → `IReadOnlyList<string>`
- `Audio.Album` → `string` (album name)
- `Audio.ParentId` → `Guid` (this IS the album's Guid — use instead of non-existent AlbumId)
- `MusicAlbum.AlbumArtists` → `IReadOnlyList<string>`
- `MusicAlbum.MusicArtist` → `MusicArtist` navigation property (gives artist entity with `.Id`)
- `MusicAlbum.AlbumArtist` → `string` (primary artist name)
- `MusicAlbum.Tracks` → `IEnumerable<Audio>`
- `Playlist.LinkedChildren` → `LinkedChild[]` (for count; query children via GetItemList)
- `SessionInfo.NowPlayingItem` → `BaseItemDto` (DTO, not entity)
- `SessionInfo.LastActivityDate` → `DateTime` (not nullable, not DateTimeOffset)
- `SessionInfo.Id` → `string` (already a string, no format specifier needed)

### GetGenres tuple
```csharp
var result = _library.GetGenres(query);
// result.Items is IReadOnlyList<(BaseItem Item, ItemCounts ItemCounts)>
var name = result.Items[0].Item1.Name;   // Item1 is BaseItem
```

### UserDataManager (synchronous!)
```csharp
var data = _userData.GetUserData(user, item);
data.IsFavorite = true;
_userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
// NOT SaveUserDataAsync — that method does not exist in 10.9.11
```

### DeleteItem
```csharp
_library.DeleteItem(item, new DeleteOptions());   // takes BaseItem, not Guid
```

## XML response pattern (CRITICAL)

XML responses MUST use attribute-style XML. The `XmlBuilder.cs` has hand-crafted builders for each endpoint. **Never** use generic JSON→XML serialization — it produces child elements which break Subsonic clients.

After adding any new endpoint, verify:
```bash
curl -s "http://localhost:8096/rest/<method>.view?u=...&p=...&v=1.16.1&c=test" | head -5
# Must show: <album id="..." name="..." .../>
# Must NOT show: <album><id>...</id><name>...</name></album>
```

## Share response rules (client crash prevention)

- **`expires`**: Always include — use `expires_at` when set, or `created_at + 1 year` as fallback. Tempus crashes if absent.
- **`visitCount`**: Always an integer. DSub2000 crashes on null/missing.
- **`url`**: Must include `?secret=...`. Build from stored encrypted secret via `SubsonicStore.GetShareSecret()`.

## Auth flow

1. `SubsonicAuth.Resolve(query)` returns `AuthResult` or `AuthError`
2. `AuthError` → return Subsonic error response immediately
3. `AuthResult` → look up `IUserManager.GetUserById(Guid.Parse(auth.JellyfinUserId))`
4. All auth uses app passwords stored in `linked_devices` table (BCrypt hash + AES-GCM encrypted plaintext)
5. Token auth (t+s) decrypts plaintext passwords from DB and computes MD5(password+salt)

## Library scoping

Every handler listing albums must check user's saved folder selection:
```csharp
private List<string>? GetEffectiveFolderIds(AuthResult auth, string? clientParam)
{
    if (!string.IsNullOrEmpty(clientParam)) return [clientParam];
    var saved = SubsonicStore.GetUserLibrarySettings(auth.SubsonicUsername);
    return saved.Count == 0 ? null : saved;  // null = no restriction
}
// Then: if (folderIds != null) query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();
```

## Artist index caching

The artist index is expensive (scans all albums). Cached in `derived_cache` with a 15-min TTL. Cache key: `artistIndex:{userId}:{folderIds}`. Stale cache is returned immediately and refreshed in background (same pattern as subfin).

## ID prefixes

Subsonic IDs use prefixes to encode item type:
- Artists: `ar-<jellyfinGuid>`
- Albums: `al-<jellyfinGuid>`
- Playlists: `pl-<jellyfinGuid>`

Stripped via `ItemMapper.StripPrefix()` before passing to Jellyfin.

## Agent Team

SME agents are in `.claude/agents/`. Same roster as subfin. Invoke before/after implementing any REST endpoint.

| Agent | When to invoke |
|-------|---------------|
| `dsub2000-sme` | Any REST endpoint — XML attribute requirements, visitCount crash |
| `tempus-sme` | Share endpoints — expires null crash |
| `opensubsonic-sme` | Validate response conformance |
| `subfin-sme` | Cross-reference patterns from the Node.js original |
| `navic-sme` | Playlist/song null field crashes |
