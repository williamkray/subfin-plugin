# CLAUDE.md — subfin-plugin

Jellyfin native Subsonic plugin (C#/.NET 9, Jellyfin 10.11.6). Ported from the Node.js `subfin` project.

## What this is

An OpenSubsonic-compatible REST API exposed as a Jellyfin plugin. Subsonic/Navidrome clients (DSub2000, Substreamer, etc.) connect to `/rest/*` on the main Jellyfin port. No separate process, no HTTP calls to Jellyfin — uses Jellyfin's DI services directly.

## Commands

```bash
dotnet build                        # must pass before any commit
dotnet test                         # run xUnit tests
./scripts/bump-version.sh 0.2.1.0   # bump version in meta.json + .csproj (four-part, single command)
./scripts/publish.sh                # build, zip, upload to home.kray.pw (production only)
./scripts/deploy-dev.sh             # dotnet publish → copy DLL into localenv → restart → smoke test
./scripts/endpoint-sweep.sh         # hit all REST endpoints and report pass/fail
node scripts/get-creds.js           # print decrypted app passwords from the plugin DB
./scripts/probe-api.sh              # compile a C# snippet against Jellyfin 10.11.6 to verify API signatures
```

## Localenv

Jellyfin runs in `../subfin/localenv/` (docker compose). The plugin dir is:
`../subfin/localenv/jellyfin-data/config/plugins/Subfin_<version>/`

**Credentials:** `node scripts/get-creds.js` decrypts app passwords from the plugin DB using
PBKDF2-SHA256 (100k iterations) + AES-256-GCM. Salt is read from
`../subfin/localenv/jellyfin-data/config/plugins/configurations/Jellyfin.Plugin.Subsonic.xml`.
Jellyfin user `user1` password: `password123`.

**Stale Jellyfin token recovery:** If the plugin gets 401 errors after Jellyfin restarts, its
stored tokens may have been invalidated. Re-link via the plugin web UI at
`http://localhost:8096/subfin/`, or get a fresh token from Jellyfin and update the DB
by running a script *inside the Jellyfin container* (host can't write to the DB — it's
owned by the container user).

**meta.json footgun:** when Jellyfin fails to load a plugin it overwrites meta.json with
`"status": "NotSupported"` and `"assemblies": []`, silently skipping it on future restarts.
`deploy-dev.sh` always resets meta.json to the known-good state.

**Third-party dep bundling:** `dotnet build` does NOT copy transitive deps to output — use
`dotnet publish`. Jellyfin ships SQLite/EF Core/ASP.NET Core; only genuinely third-party
packages need bundling (currently: `BCrypt.Net-Next.dll`). Each bundled DLL must be listed
in `meta.json "assemblies"[]`, otherwise Jellyfin's plugin load context won't resolve it.

**Checking what Jellyfin already ships:** `docker exec jellyfin ls /jellyfin/` — avoid
bundling anything already there to keep the plugin dir minimal.

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
| `Controllers/WebController.cs` | `/subfin/*` web UI: device management, library selection, share pages |
| `Store/SubsonicStore.cs` | All SQLite access (static class) |
| `Store/Crypto.cs` | AES-256-GCM encrypt/decrypt; PBKDF2-SHA256 key derivation |
| `Store/Schema.sql` | DB schema (embedded resource) |
| `Auth/SubsonicAuth.cs` | Resolves u/p, u/t/s, apiKey, share auth → AuthResult |
| `Mappers/ItemMapper.cs` | Maps Jellyfin entities → OpenSubsonic response shapes |
| `Response/SubsonicResponse.cs` | Constants, SubsonicEnvelope.Ok/Error |
| `Response/XmlBuilder.cs` | Hand-crafted attribute-style XML builders per endpoint |

### URL paths
- `/rest/{method}` — All Subsonic API endpoints
- `/subfin/` — Web UI (device management)
- `/subfin/share/{uid}` — Public share pages
- `/subfin/api/*` — Web UI REST API

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
- `MusicAlbum.MusicArtist` → `MusicArtist` navigation property (**folder-hierarchy entity** — see warning below)
- `MusicAlbum.AlbumArtist` → `string` (primary artist name)
- `MusicAlbum.Tracks` → `IEnumerable<Audio>`
- `Playlist.LinkedChildren` → `LinkedChild[]` (use `lc.ItemId` — Guid? — to identify items; `lc.Path` is the stored file path; `lc.LibraryItemId` is usually null)
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
// Must set DeleteFileLocation = true — otherwise only removes from index, file stays on disk
_library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
```

### IPlaylistManager — add/remove items
```csharp
// Add songs to a playlist
await _playlists.AddItemToPlaylistAsync(playlistGuid, songGuidArray, user.Id);

// Remove songs by entry ID (NOT index)
// entryId = LinkedChild.ItemId?.ToString("N")  — no-dash Guid format, MUST match exactly
// LibraryItemId is null in most cases; ItemId is the Jellyfin audio item Guid
await _playlists.RemoveItemFromPlaylistAsync(playlistGuid.ToString("N"), entryIds);

// To remove by Subsonic songIndexToRemove:
var children = pl.LinkedChildren ?? Array.Empty<LinkedChild>();
var entryIds = indexesToRemove
    .Where(i => i < children.Length)
    .Select(i => children[i].ItemId?.ToString("N"))
    .Where(s => !string.IsNullOrEmpty(s))
    .Cast<string>().ToList();
```

## Jellyfin plugin config page (`config.html`) patterns

`config.html` is served by Jellyfin at `/web/configurationpage?name=SubsonicAdmin` via `Plugin.GetPages()`. It is loaded into Jellyfin's SPA — **not** as a full page navigation.

**Critical DOM requirement:** The outermost element MUST be `<div data-role="page" class="page type-interior withSideMenu">`. Jellyfin's `loadView()` looks up this element by `data-role` to apply SPA lifecycle; without it, `h.classList` throws and the entire page fails to load.

**Script placement is critical:** `<script>` tags MUST be placed **inside** the `<div data-role="page">` element. Jellyfin's `loadView()` only processes content within that div — scripts placed outside (e.g. at the bottom of `<body>`) are silently ignored and never executed.

**Script execution is unreliable:** Even when placed correctly inside the div, execution is via eval. Symptoms of scripts not executing: static initial text stays forever, no console errors. Do NOT diagnose by reasoning about async failures; first confirm scripts run by adding a synchronous DOM mutation at the top of the script (e.g., change a heading text). If the text doesn't change after deploy, scripts aren't executing.

**Event listener registration must happen inside `pagebeforeshow`:** Registering event listeners (e.g. form submit) directly in script body is unsafe — the DOM element may not be ready. Register them inside the `pagebeforeshow` handler instead, where the DOM is guaranteed available.

**`ApiClient` is available** in config pages (Jellyfin injects it globally). Use `ApiClient.accessToken()` for auth, not `localStorage`. The `pagebeforeshow` event fires reliably for config/settings loading.

**`is="emby-*"` attributes** (`is="emby-input"`, `is="emby-checkbox"`, `is="emby-button"`) trigger Jellyfin's webcomponents polyfill. The polyfill may throw `Uncaught TypeError: can't access property "htmlFor"` during element upgrade — this error is harmless (cosmetic only) as long as scripts are correctly placed inside `data-role="page"`. It does NOT abort script execution in that context.

**Keep config.html minimal.** Features that require dynamic data fetched by inline JS are risky. If the data is also available in `/subfin/` (index.html), omit it from config.html rather than duplicating with fragile inline fetches.

## Web UI auth pattern

The `/subfin/` page uses the Jellyfin session — no separate login.

**Frontend** reads token from `localStorage["jellyfin_credentials"]`:
```javascript
const raw = localStorage.getItem('jellyfin_credentials');
const token = JSON.parse(raw)?.Servers?.[0]?.AccessToken;
// Send as: Authorization: MediaBrowser Token="<token>"
// Redirect on missing/invalid token: window.location.replace('/web/index.html#!/login.html')
```

**Backend** uses `[Authorize(AuthenticationSchemes = "CustomAuthentication")]`:
```csharp
var userIdStr = User.FindFirst("Jellyfin-UserId")?.Value;  // claim set by Jellyfin's auth middleware
Guid.TryParse(userIdStr, out var userId);
var user = _userManager.GetUserById(userId);
```

**JSON casing gotcha:** Jellyfin serializes JSON with the property names as written in C#.
- Named types (records, classes) → PascalCase (`Id`, `DeviceLabel`, `CreatedAt`)
- Anonymous objects → camelCase as written (`deviceId`, `subsonicUsername`, `password`)
Keep frontend field references consistent with this: `d.Id`, `d.DeviceLabel` for record
responses; `d.deviceId`, `d.password` for anonymous object responses. Mixing these up causes
silent `undefined` values that produce requests to `.../undefined`.

## XML response pattern (CRITICAL)

XML responses MUST use attribute-style XML. The `XmlBuilder.cs` has hand-crafted builders for each endpoint. **Never** use generic JSON→XML serialization — it produces child elements which break Subsonic clients.

**XmlWriter ordering rule:** In `XmlWriter`, all `WriteAttributeString` calls for an element MUST come before any `WriteStartElement` child. Writing an attribute after a child element has been opened and closed throws `InvalidOperationException` at runtime. Pattern:
```csharp
w.WriteStartElement("artistInfo", Ns);
w.WriteAttributeString("musicBrainzId", mbid);  // attributes first
w.WriteAttributeString("lastFmUrl", url);
if (!string.IsNullOrEmpty(bio)) { w.WriteStartElement("biography", Ns); w.WriteString(bio); w.WriteEndElement(); }  // children after
w.WriteEndElement();
```

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

## Jellyfin artist entity model (CRITICAL)

Jellyfin maintains **two separate MusicArtist entity populations** that serve different purposes:

| Type | Example name | Has parent? | Returned by `GetItemList(MusicArtist, Recursive=true)`? | Used by `AlbumArtistIds` filter? |
|------|-------------|-------------|-------------------------------------------------------|----------------------------------|
| **Folder/hierarchy entity** | `_NSYNC` | Yes (library folder) | ✅ | ❌ |
| **Tag/index entity** | `*NSYNC` | No (parentless) | ❌ | ✅ |

- `album.MusicArtist` nav prop → **folder entity** (name may be Jellyfin-normalized, e.g. `*` → `_`)
- `AlbumArtistIds = [guid]` in `InternalItemsQuery` → does a name-lowered match against `ItemValues.CleanValue`; only the **tag entity** (whose lowercased name matches the stored CleanValue) finds albums
- **`_library.GetArtist(string name)`** → returns the **tag entity** for that name (the same entity Jellyfin uses when building album DTOs' `ArtistItems`). This is the correct method for resolving artist names to IDs that will work with `AlbumArtistIds`.

**Rule:** Never use `album.MusicArtist?.Id` as the artist ID in Subsonic responses. Always use `_library.GetArtist(album.AlbumArtist)?.Id`. The folder entity and tag entity for the same artist are different Guids; using the wrong one causes `getArtist` to return 0 albums.

## Artist index caching

The artist index is expensive (scans all albums). Cached in `derived_cache` with a 15-min TTL. Cache key: `artistIndex:{userId}:{folderIds}`. Stale cache is returned immediately and refreshed in background (same pattern as subfin).

**Testing after deploy:** Always clear `derived_cache` before validating artist-related fixes, otherwise stale cache masks the change:
```bash
sqlite3 ../subfin/localenv/jellyfin-data/config/data/SubfinPlugin/subsonic.db "DELETE FROM derived_cache;"
```

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
| `navic-sme` | Any JSON endpoint — typed field crashes, mediaType validation, Instant format, MusicFolder int ID |

## Navic (subsonic-kotlin) compatibility rules

Navic uses strict typed JSON deserialization via kotlinx.serialization. Violations crash the entire response parse, not just the offending field. When adding any endpoint, validate against navic-sme.

**`mediaType` — enum, required on every song entry:**
Valid values: `"song"`, `"album"`, `"artist"`. Value `"audio"` or absent field → crash.
Applies to: `getShares`, `getDirectory`, `getPlayQueue`, any `List<SubsonicResource>` field.

**`MusicFolder.id` — must be `Int`, not String:**
Navic's `MusicFolder.id: Int`. Jellyfin folder IDs are GUIDs. Convert via `FolderIdToInt()` in JSON responses; XML can keep the GUID. Reverse-map int→GUID in `GetEffectiveFolderIds` for incoming `musicFolderId` params.

**Instant fields — ISO 8601 with `T` and `Z` required:**
`"2026-03-26T05:23:46Z"` is valid. `"2026-03-26 05:23:46"` (space separator, no Z) crashes. Use `"yyyy-MM-ddTHH:mm:ssZ"` or `ToString("o")` (includes fractional seconds — also valid).
Affects: `Share.created`, `Share.expires`, `Album.created`, `Playlist.created`, `Playlist.changed`.

**`artistId` — required non-nullable String on Song and Album:**
`ResolveArtistTagId()` can return null. Always use `artistId ?? ""` in `ItemMapper.ToSong`, `ToAlbumShort`, `ToAlbum`.

**JSON key ordering — envelope:**
Navic reads `.entries.last()` for the payload key. Use `JsonObject` (insertion-ordered), not `Dictionary<string,object>`, so `openSubsonic` and other metadata keys serialize before the payload key.

## Diagnostic patterns

**Item shows in Subsonic client but not in Jellyfin UI** → item exists on disk but is missing from Jellyfin's index. Likely caused by `DeleteItem` called without `DeleteFileLocation = true` — Jellyfin re-discovers orphaned files on next library scan. Check: `ls localenv/jellyfin-data/config/data/playlists/`. Fix: delete manually or re-call `deletePlaylist` after deploying the `DeleteFileLocation = true` fix.

**Albums from wrong library appearing** → `[JF] getAlbumsByArtist request ... musicFolderId=` empty in logs — calling handler not applying library scoping pattern.

**`getIndexes` slow on every request** → handler bypassing `getOrBuildArtistIndex` cache. Fix: use `GetArtistsIndex` path that checks `derived_cache`.

**Client crashes on playlist/song responses with "Expected string, got null"** → required Subsonic string field is null from Jellyfin. Always provide fallbacks: `song.Name ?? ""`, `song.Album ?? ""`, etc.

**`getArtist` returns 0 albums for an artist that appears in `getArtists`** → `BuildArtistList` used `album.MusicArtist?.Id` (folder entity) instead of `_library.GetArtist(name)?.Id` (tag entity). The wrong entity's Guid doesn't match what `AlbumArtistIds` queries against. See Jellyfin artist entity model above.

**Artist index fix deployed but `getArtists` still shows old data** → `derived_cache` is stale (15-min TTL). Clear it: `sqlite3 .../SubfinPlugin/subsonic.db "DELETE FROM derived_cache;"` then re-request.

**Web UI shows "Loading…" indefinitely with no console errors** → the inline `<script>` block is not executing at all (Jellyfin SPA injection swallowed it). To confirm: add a synchronous DOM mutation as the very first line of the script (e.g., `document.title = 'ran';`). If it doesn't change, the script never ran. Fix: use `pagebeforeshow` event and `ApiClient.accessToken()` rather than direct calls and `localStorage`; or remove the feature from `config.html` if it's covered by `/subfin/`.

**Web UI "Loading…" stuck after confirmed script execution** → fetch is either pending or the `.then`/`.catch` chain is failing silently. First add `r.text()` instead of `r.json()` and log/display the raw response to see what the server is actually returning before reasoning about handling logic.

## After any code change (mandatory)

1. Deploy + sweep: `./scripts/deploy-dev.sh && ./scripts/endpoint-sweep.sh`
2. Verify no new failures (the `getPlaylist` failure is a pre-existing test-data gap — expected)
3. Update `current-plan.md` and the relevant `issues/*.md` file before considering the task done

Do not wait for the user to ask. These three steps are part of completing any fix.

## Publishing for external (prod) testing

**Always bump the version before publishing** — `serverVersion` in the ping response is dynamic (reads from the loaded plugin assembly), so version changes are immediately verifiable:

```bash
./scripts/bump-version.sh 0.2.1.0  # four-part version required
./scripts/deploy-dev.sh             # smoke test shows new serverVersion
./scripts/publish.sh
```

On prod after install + Jellyfin restart, confirm the right code is loaded:
```bash
curl "http://server/rest/ping.view?u=...&p=...&v=1.16.1&c=test"
# Must show serverVersion="0.2.1.0" before testing any behavior
```

If `serverVersion` still shows the old value, Jellyfin is still running old code — do not test behavior.

## Jellyfin HTTP audio proxy pattern

When proxying to Jellyfin's audio endpoints for transcoding, use `/Audio/{id}/stream.{container}` — the container is in the path and Jellyfin uses it literally:

```
/Audio/{guid}/stream.mp3?audioCodec=mp3&audioBitRate=128000&static=false&userId=...&deviceId=...
/Audio/{guid}/stream.webm?audioCodec=opus&audioBitRate=64000&static=false&userId=...&deviceId=...
```

**Do not use `/Audio/{id}/universal` for transcoding.** The `container` query param describes direct-play capability only; the transcoding output container defaults to `mp3` regardless of what you send (even `transcodingContainer` is silently ignored). This produces invalid profiles like `mp3+opus` which ffmpeg rejects with exit code 234.

Always add diagnostic logging before testing proxy code:
```csharp
_logger.LogInformation("[Subfin] stream proxy → {Url}", url);
```
Without this, a version mismatch on prod is indistinguishable from a logic bug.
