using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Handles /subfin/* web UI routes: device management, library selection,
/// Quick Connect linking, and public share pages.
/// </summary>
[ApiController]
[Route("subfin")]
public class WebController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _library;
    private readonly ILogger<WebController> _logger;

    public WebController(IUserManager userManager, ILibraryManager library, ILogger<WebController> logger)
    {
        _userManager = userManager;
        _library = library;
        _logger = logger;
    }

    // ── Index ────────────────────────────────────────────────────────────────

    [HttpGet("")]
    [HttpGet("index")]
    public IActionResult Index()
    {
        // BaseUrl-aware paths for the page's fetch() calls and login redirect
        var html = GetEmbeddedHtml("index.html")
            .Replace("{{BASE_PATH_JSON}}", JsonSerializer.Serialize(Urls.BasePath(Request)));
        return Content(html, "text/html; charset=utf-8");
    }

    // ── Share public page ────────────────────────────────────────────────────

    [HttpGet("share/{uid}")]
    public IActionResult SharePage(string uid)
    {
        var secret = Request.Query["secret"].ToString();
        var (status, error, share, owner) = ResolvePublicShare(uid, secret, PermissionKind.EnableMediaPlayback);
        if (error != null) return StatusCode(status, error);

        SubsonicStore.IncrementShareVisitCount(uid);

        var baseUrl = Urls.PublicBase(Request);
        var tracks = share.EntryIdsFlat.Select(id =>
        {
            if (!Guid.TryParse(id, out var guid)) return null;
            var audio = _library.GetItemById<Audio>(guid, owner);
            if (audio == null) return null;
            var duration = ItemMapper.TicksToSeconds(audio.RunTimeTicks);
            var artist = audio.AlbumArtists.FirstOrDefault() ?? audio.Artists.FirstOrDefault() ?? "";
            var streamUrl = $"{baseUrl}/rest/stream.view?id={guid:N}&u=share_{uid}&p={Uri.EscapeDataString(secret)}&v=1.16.1&c=subfin-share";
            return new { title = audio.Name ?? "", artist, album = audio.Album ?? "", duration, streamUrl };
        }).Where(t => t != null).ToList();
        var tracksJson = JsonSerializer.Serialize(tracks);

        var html = GetEmbeddedHtml("share.html")
            .Replace("{{BASE_PATH}}", System.Net.WebUtility.HtmlEncode(Urls.BasePath(Request)))
            .Replace("{{SHARE_UID}}", uid)
            .Replace("{{SECRET}}", System.Net.WebUtility.HtmlEncode(secret))
            .Replace("{{TRACKS_JSON}}", tracksJson)
            .Replace("{{DESCRIPTION}}", System.Net.WebUtility.HtmlEncode(share.Description ?? "Shared Music"));
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Validates a public share request (page / m3u / zip): sharing enabled, share exists,
    /// secret matches (constant time), not expired, and the owning device + Jellyfin user still
    /// exist, are not disabled and hold <paramref name="required"/>. Track lookups must then use
    /// the returned owner so library access / parental controls apply.
    /// </summary>
    private (int Status, string? Error, ShareRecord Share, User Owner) ResolvePublicShare(string uid, string secret, PermissionKind required)
    {
        if (SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false) return (404, "Not found.", null!, null!);
        var share = SubsonicStore.GetShare(uid);
        if (share == null) return (404, "Share not found.", null!, null!);
        if (!SubsonicAuth.SecretEquals(SubsonicStore.GetShareSecret(uid), secret)) return (401, "Invalid share link.", null!, null!);
        if (SubsonicAuth.IsShareExpired(share.ExpiresAt)) return (410, "This share has expired.", null!, null!);

        var device = SubsonicStore.GetDeviceById(share.LinkedDeviceId);
        var owner = device != null && Guid.TryParse(device.JellyfinUserId, out var ownerId) ? _userManager.GetUserById(ownerId) : null;
        if (owner == null || owner.HasPermission(PermissionKind.IsDisabled) || !owner.HasPermission(required))
            return (404, "Share not found.", null!, null!);
        return (200, null, share, owner);
    }

    // ── API: session ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current Jellyfin user's id and display name.
    /// Requires a valid Jellyfin session token in the Authorization header:
    ///   Authorization: MediaBrowser Token="&lt;token&gt;"
    /// </summary>
    [HttpGet("api/me")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult GetMe()
    {
        var userIdStr = User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
        var user = _userManager.GetUserById(userId);
        if (user == null) return Unauthorized();
        return Ok(new { id = user.Id.ToString("N"), name = user.Username });
    }

    // ── API: device management ───────────────────────────────────────────────

    [HttpGet("api/devices")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult ListDevices()
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        var devices = SubsonicStore.GetDevicesByJellyfinUserId(user.Id.ToString("N"))
            .Where(d => d.JellyfinDeviceId != SubsonicStore.WebShareDeviceSentinel);
        return Ok(devices.Select(d => new { d.Id, d.DeviceLabel, d.SubsonicUsername, d.CreatedAt }));
    }

    /// <summary>
    /// Links a new device using the active Jellyfin session; Jellyfin username
    /// becomes the Subsonic username — no separate field needed.
    /// </summary>
    [HttpPost("api/devices/link")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult LinkDevice([FromBody] LinkDeviceRequest req)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;

        var password = GeneratePassword();
        var deviceId = SubsonicStore.InsertDevice(user.Username, user.Id.ToString("N"), password, req.DeviceLabel ?? "", null, null);

        return Ok(new { deviceId, subsonicUsername = user.Username, password, message = "Device linked. Save this password — it will not be shown again." });
    }

    [HttpPost("api/devices/{id}/rename")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult RenameDevice(long id, [FromBody] RenameRequest req)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!OwnedBy(id, user)) return Forbid();
        SubsonicStore.UpdateDeviceLabel(id, req.Label ?? "");
        return Ok();
    }

    [HttpPost("api/devices/{id}/reset-password")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult ResetPassword(long id)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!OwnedBy(id, user)) return Forbid();
        var device = SubsonicStore.GetDeviceById(id);
        var password = GeneratePassword();
        SubsonicStore.UpdateDevicePassword(id, password);
        return Ok(new { subsonicUsername = device?.SubsonicUsername ?? "", password, message = "Password reset. Save this password — it will not be shown again." });
    }

    [HttpDelete("api/devices/{id}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult DeleteDevice(long id)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!OwnedBy(id, user)) return Forbid();
        SubsonicStore.DeleteDevice(id);
        return Ok();
    }

    // ── API: QuickConnect linking ────────────────────────────────────────────

    [HttpPost("api/quickconnect/start")]
    public IActionResult StartQuickConnect([FromBody] QuickConnectStartRequest req)
    {
        if (string.IsNullOrEmpty(req.SubsonicUsername)) return BadRequest("subsonicUsername required");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace("+", "-").Replace("/", "_").Replace("=", "");
        return Ok(new { secret, message = "Approve this in the Jellyfin web UI, then call /api/quickconnect/complete" });
    }

    [HttpPost("api/quickconnect/complete")]
    public IActionResult CompleteQuickConnect([FromBody] QuickConnectCompleteRequest req)
    {
        if (string.IsNullOrEmpty(req.Secret) || string.IsNullOrEmpty(req.SubsonicUsername)) return BadRequest("Missing fields");

        var jellyfinUserId = SubsonicStore.ConsumePendingQuickConnect(req.Secret);
        if (jellyfinUserId == null) return BadRequest("QuickConnect secret not found or expired.");

        var password = GeneratePassword();
        var deviceId = SubsonicStore.InsertDevice(req.SubsonicUsername, jellyfinUserId, password, req.DeviceLabel ?? "Quick Connect", null, null);
        return Ok(new { deviceId, password });
    }

    // ── API: user share management ───────────────────────────────────────────

    [HttpGet("api/shares")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult GetMyShares()
    {
        if (SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false) return NotFound();
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        var shares = SubsonicStore.GetSharesForJellyfinUser(user.Id.ToString("N"));
        var baseUrl = Urls.PublicBase(Request);
        return Ok(shares.Select(s => {
            var secret = SubsonicStore.GetShareSecret(s.ShareUid) ?? "";
            return new {
                uid = s.ShareUid,
                description = s.Description,
                created = s.CreatedAt,
                expires = s.ExpiresAt,
                visitCount = s.VisitCount,
                url = $"{baseUrl}/subfin/share/{s.ShareUid}?secret={Uri.EscapeDataString(secret)}",
            };
        }));
    }

    // ── API: create share (search + create) ──────────────────────────────────

    /// <summary>
    /// Searches the current user's libraries for artists, albums, songs and playlists.
    /// Returns Subsonic-prefixed IDs (ar-/al-/pl-/bare track) ready to pass to
    /// POST api/shares. Substring match, library-scoped like the Subsonic search3.
    /// </summary>
    [HttpGet("api/search")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult Search([FromQuery] string? query, [FromQuery] int count = 20)
    {
        if (SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false) return NotFound();
        var (user, err) = ResolveUser();
        if (user == null) return err!;

        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return Ok(new { artists = Array.Empty<object>(), albums = Array.Empty<object>(), songs = Array.Empty<object>(), playlists = Array.Empty<object>() });

        count = Math.Clamp(count, 1, 50);
        var folderIds = EffectiveFolderIds(user.Username);

        // Artists: reuse the cached tag-entity index (same key search3/getIndexes populate)
        // so IDs work with AlbumArtistIds; fall back to building it fresh on a cache miss.
        var cacheKey = $"artistIndex:{user.Id:N}:{(folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x)))}";
        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        IEnumerable<(string Id, string Name, int AlbumCount)> artistIndex = cached != null
            ? (JsonSerializer.Deserialize<List<ArtistIndexEntry>>(cached.ValueJson) ?? []).Select(a => (a.Id, a.Name, a.AlbumCount))
            : LibraryQueries.BuildArtistList(_library, user, folderIds);

        var artists = artistIndex
            .Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(count)
            .Select(a => new { id = $"ar-{a.Id}", name = a.Name, albumCount = a.AlbumCount })
            .ToList();

        var albumQuery = new InternalItemsQuery(user)
        { SearchTerm = q, IncludeItemTypes = [BaseItemKind.MusicAlbum], Limit = count, Recursive = true };
        if (folderIds != null) albumQuery.AncestorIds = folderIds.Select(Guid.Parse).ToArray();
        var albums = _library.GetItemList(albumQuery).OfType<MusicAlbum>()
            .Select(a => new
            {
                id = $"al-{a.Id:N}",
                name = a.Name ?? "",
                artist = a.AlbumArtist ?? a.AlbumArtists.FirstOrDefault() ?? "",
                year = a.ProductionYear,
            }).ToList();

        var songQuery = new InternalItemsQuery(user)
        { SearchTerm = q, IncludeItemTypes = [BaseItemKind.Audio], Limit = count, Recursive = true };
        if (folderIds != null) songQuery.AncestorIds = folderIds.Select(Guid.Parse).ToArray();
        var songs = _library.GetItemList(songQuery).OfType<Audio>()
            .Select(s => new
            {
                id = s.Id.ToString("N"),
                title = s.Name ?? "",
                artist = s.AlbumArtists.FirstOrDefault() ?? s.Artists.FirstOrDefault() ?? "",
                album = s.Album ?? "",
            }).ToList();

        var playlists = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Playlist], Recursive = true }).OfType<Playlist>()
            .Where(pl => (pl.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(count)
            .Select(pl => new { id = $"pl-{pl.Id:N}", name = pl.Name ?? "", songCount = pl.LinkedChildren?.Length ?? 0 })
            .ToList();

        return Ok(new { artists, albums, songs, playlists });
    }

    /// <summary>
    /// Creates a share from selected search-result IDs. The share is owned by a hidden
    /// per-user pseudo-device (see SubsonicStore.GetOrCreateWebShareDevice) so it appears
    /// in the user's share list without requiring a real linked Subsonic client.
    /// </summary>
    [HttpPost("api/shares")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult CreateShare([FromBody] CreateShareRequest req)
    {
        if (SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false) return NotFound();
        var (user, err) = ResolveUser();
        if (user == null) return err!;

        var ids = (req.Ids ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (ids.Count == 0) return BadRequest("No items selected.");

        var flat = LibraryQueries.ExpandShareIds(_library, user, ids);
        if (flat.Count == 0) return BadRequest("Selected items contain no playable tracks.");

        string? expiresAt = null;
        if (!string.IsNullOrEmpty(req.Expires) && long.TryParse(req.Expires, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");

        var desc = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        var deviceId = SubsonicStore.GetOrCreateWebShareDevice(user.Username, user.Id.ToString("N"));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace("+", "-").Replace("/", "_").Replace("=", "");
        var uid = SubsonicStore.InsertShare(deviceId, ids, flat, desc, expiresAt, secret);

        var baseUrl = Urls.PublicBase(Request);
        var url = $"{baseUrl}/subfin/share/{uid}?secret={Uri.EscapeDataString(secret)}";
        return Ok(new { uid, url, description = desc, expires = expiresAt, songCount = flat.Count });
    }

    [HttpDelete("api/shares/{uid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult DeleteMyShare(string uid)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        var share = SubsonicStore.GetShare(uid);
        if (share == null) return NotFound();
        // Verify this share belongs to the current user
        var shares = SubsonicStore.GetSharesForJellyfinUser(user.Id.ToString("N"));
        if (!shares.Any(s => s.ShareUid == uid)) return Forbid();
        SubsonicStore.DeleteShare(uid);
        return Ok();
    }

    // ── API: admin share management ──────────────────────────────────────────

    [HttpGet("api/admin/shares")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult GetAllSharesAdmin()
    {
        if (SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false) return NotFound();
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)) return Forbid();
        var shares = SubsonicStore.GetAllShares();
        var baseUrl = Urls.PublicBase(Request);
        return Ok(shares.Select(t => {
            var secret = SubsonicStore.GetShareSecret(t.Share.ShareUid) ?? "";
            return new {
                uid = t.Share.ShareUid, username = t.SubsonicUsername,
                description = t.Share.Description,
                created = t.Share.CreatedAt, expires = t.Share.ExpiresAt,
                visitCount = t.Share.VisitCount,
                url = $"{baseUrl}/subfin/share/{t.Share.ShareUid}?secret={Uri.EscapeDataString(secret)}",
            };
        }));
    }

    [HttpDelete("api/admin/shares/{uid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult AdminDeleteShare(string uid)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)) return Forbid();
        SubsonicStore.DeleteShare(uid);
        return Ok();
    }

    [HttpPatch("api/shares/{uid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult RenameMyShare(string uid, [FromBody] RenameShareRequest req)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        var shares = SubsonicStore.GetSharesForJellyfinUser(user.Id.ToString("N"));
        if (!shares.Any(s => s.ShareUid == uid)) return Forbid();
        SubsonicStore.UpdateShareDescription(uid, req.Description);
        return Ok();
    }

    [HttpPatch("api/admin/shares/{uid}")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult AdminRenameShare(string uid, [FromBody] RenameShareRequest req)
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)) return Forbid();
        SubsonicStore.UpdateShareDescription(uid, req.Description);
        return Ok();
    }

    // ── Share: M3U download ──────────────────────────────────────────────────

    [HttpGet("share/{uid}/m3u")]
    public IActionResult ShareM3u(string uid)
    {
        var secret = Request.Query["secret"].ToString();
        var (status, error, share, owner) = ResolvePublicShare(uid, secret, PermissionKind.EnableMediaPlayback);
        if (error != null) return StatusCode(status, error);

        SubsonicStore.IncrementShareVisitCount(uid);

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        var baseUrl = Urls.PublicBase(Request);
        foreach (var id in share.EntryIdsFlat)
        {
            if (!Guid.TryParse(id, out var guid)) continue;
            var audio = _library.GetItemById<Audio>(guid, owner);
            if (audio == null) continue;
            var duration = ItemMapper.TicksToSeconds(audio.RunTimeTicks);
            var artist = audio.AlbumArtists.FirstOrDefault() ?? "";
            var title = audio.Name ?? "";
            sb.AppendLine($"#EXTINF:{duration},{artist} - {title}");
            sb.AppendLine($"{baseUrl}/rest/stream.view?id={guid:N}&u=share_{uid}&p={Uri.EscapeDataString(secret)}&v=1.16.1&c=subfin-share");
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "audio/x-mpegurl", $"share-{uid}.m3u8");
    }

    // ── Share: ZIP download ───────────────────────────────────────────────────

    [HttpGet("share/{uid}/download")]
    public async Task ShareZip(string uid)
    {
        var secret = Request.Query["secret"].ToString();
        var (status, error, share, owner) = ResolvePublicShare(uid, secret, PermissionKind.EnableContentDownloading);
        if (error != null) { Response.StatusCode = status; return; }

        var m3u = new StringBuilder("#EXTM3U\n");
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var songEntries = new List<(Audio audio, string fileName)>();

        // Resolve songs and deduplicate filenames
        foreach (var id in share.EntryIdsFlat)
        {
            if (!Guid.TryParse(id, out var guid)) continue;
            var audio = _library.GetItemById<Audio>(guid, owner);
            if (audio?.Path == null) continue;

            var baseName = Path.GetFileName(audio.Path);
            string fileName;
            if (!usedNames.ContainsKey(baseName))
            {
                usedNames[baseName] = 0;
                fileName = baseName;
            }
            else
            {
                usedNames[baseName]++;
                var ext = Path.GetExtension(baseName);
                var stem = Path.GetFileNameWithoutExtension(baseName);
                fileName = $"{stem} ({usedNames[baseName]}){ext}";
            }
            songEntries.Add((audio, fileName));
        }

        // Build ZIP into a MemoryStream first: ZipArchive.Dispose() writes the central
        // directory synchronously, which Kestrel rejects when writing directly to Response.Body.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (song, fileName) in songEntries)
            {
                var duration = (int)((song.RunTimeTicks ?? 0) / 10_000_000);
                var artist = song.AlbumArtists.FirstOrDefault() ?? "";
                m3u.Append($"#EXTINF:{duration},{artist} - {song.Name ?? ""}\n");
                m3u.Append(fileName + "\n");

                var entry = zip.CreateEntry(fileName, CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                using var fileStream = System.IO.File.OpenRead(song.Path!);
                await fileStream.CopyToAsync(entryStream);
            }

            var m3uEntry = zip.CreateEntry("playlist.m3u8");
            using var m3uStream = new StreamWriter(m3uEntry.Open(), Encoding.UTF8);
            await m3uStream.WriteAsync(m3u.ToString());
        } // Dispose writes central directory to ms (sync is fine — ms is in-memory)

        Response.ContentType = "application/zip";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"share-{uid}.zip\"";
        Response.ContentLength = ms.Length;
        ms.Position = 0;
        await ms.CopyToAsync(Response.Body);
    }

    // ── API: library selection ───────────────────────────────────────────────

    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [HttpGet("api/libraries")]
    public IActionResult GetLibraries([FromQuery] string username)
    {
        if (string.IsNullOrEmpty(username)) return BadRequest("username required");
        var (currentUser, err) = ResolveUser();
        if (err != null) return err;

        var devices = SubsonicStore.GetDevicesForUser(username);
        if (devices.Count == 0) return BadRequest("No linked devices for this username.");
        if (devices[0].JellyfinUserId != currentUser!.Id.ToString("N")) return Forbid();

        var allFolders = _library.GetVirtualFolders()
            .Where(f => f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
            .Select(f => new { id = f.ItemId, name = f.Name ?? "" });
        var selected = SubsonicStore.GetUserLibrarySettings(username);
        return Ok(new { folders = allFolders, selected });
    }

    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    [HttpPost("api/libraries")]
    public IActionResult SetLibraries([FromBody] SetLibrariesRequest req)
    {
        if (string.IsNullOrEmpty(req.Username)) return BadRequest("username required");
        var (currentUser, err) = ResolveUser();
        if (err != null) return err;

        var devices = SubsonicStore.GetDevicesForUser(req.Username);
        if (devices.Count == 0) return BadRequest("No linked devices for this username.");
        if (devices[0].JellyfinUserId != currentUser!.Id.ToString("N")) return Forbid();

        SubsonicStore.SetUserLibrarySettings(req.Username, req.SelectedIds ?? []);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Resolve the current Jellyfin user from claims, or return an error result.</summary>
    private (User? user, IActionResult? error) ResolveUser()
    {
        var userIdStr = User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(userIdStr, out var userId)) return (null, Unauthorized());
        var user = _userManager.GetUserById(userId);
        return user == null ? (null, Unauthorized()) : (user, null);
    }

    /// <summary>Resolve the user's saved library folder selection, or null for no restriction.</summary>
    private static List<string>? EffectiveFolderIds(string subsonicUsername)
    {
        var saved = SubsonicStore.GetUserLibrarySettings(subsonicUsername);
        return saved.Count == 0 ? null : saved;
    }

    /// <summary>Returns true if the device with the given id belongs to the given user.</summary>
    private static bool OwnedBy(long deviceId, User user)
    {
        var device = SubsonicStore.GetDeviceById(deviceId);
        return device != null && device.JellyfinUserId == user.Id.ToString("N");
    }

    private static string GetEmbeddedHtml(string filename)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"Jellyfin.Plugin.Subsonic.Web.Views.{filename}";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return $"<html><body><h1>subfin-plugin</h1><p>View {filename} not found.</p></body></html>";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace("+", "a").Replace("/", "b").Replace("=", "c");
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record LinkDeviceRequest(string? DeviceLabel);
public record RenameRequest(string? Label);
public record QuickConnectStartRequest(string SubsonicUsername);
public record QuickConnectCompleteRequest(string Secret, string SubsonicUsername, string? DeviceLabel);
public record SetLibrariesRequest(string Username, List<string>? SelectedIds);
public record RenameShareRequest(string? Description);
public record CreateShareRequest(List<string>? Ids, string? Description, string? Expires);

/// <summary>Shape of a cached artist-index entry (mirrors SubsonicController's ArtistCacheEntry).</summary>
public record ArtistIndexEntry(string Id, string Name, int AlbumCount);
