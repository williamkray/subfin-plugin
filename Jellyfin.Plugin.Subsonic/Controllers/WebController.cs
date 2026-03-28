using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Handles /Subsonic/* web UI routes: device management, library selection,
/// Quick Connect linking, and public share pages.
/// </summary>
[ApiController]
[Route("Subsonic")]
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
        var html = GetEmbeddedHtml("index.html");
        return Content(html, "text/html; charset=utf-8");
    }

    // ── Share public page ────────────────────────────────────────────────────

    [HttpGet("share/{uid}")]
    public IActionResult SharePage(string uid)
    {
        var secret = Request.Query["secret"].ToString();
        var share = SubsonicStore.GetShare(uid);
        if (share == null) return NotFound("Share not found.");

        // Validate secret
        var storedSecret = SubsonicStore.GetShareSecret(uid);
        if (storedSecret != secret) return Unauthorized("Invalid share link.");

        // Check expiry
        if (!string.IsNullOrEmpty(share.ExpiresAt) && DateTimeOffset.Parse(share.ExpiresAt) < DateTimeOffset.UtcNow)
            return BadRequest("This share has expired.");

        SubsonicStore.IncrementShareVisitCount(uid);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var tracks = share.EntryIdsFlat.Select(id =>
        {
            if (!Guid.TryParse(id, out var guid)) return null;
            var audio = _library.GetItemById<Audio>(guid);
            if (audio == null) return null;
            var duration = ItemMapper.TicksToSeconds(audio.RunTimeTicks);
            var artist = audio.AlbumArtists.FirstOrDefault() ?? audio.Artists.FirstOrDefault() ?? "";
            var streamUrl = $"{baseUrl}/rest/stream.view?id={guid:N}&u=share_{uid}&p={Uri.EscapeDataString(secret)}&v=1.16.1&c=subfin-share";
            return new { title = audio.Name ?? "", artist, album = audio.Album ?? "", duration, streamUrl };
        }).Where(t => t != null).ToList();
        var tracksJson = JsonSerializer.Serialize(tracks);

        var html = GetEmbeddedHtml("share.html")
            .Replace("{{SHARE_UID}}", uid)
            .Replace("{{SECRET}}", System.Net.WebUtility.HtmlEncode(secret))
            .Replace("{{TRACKS_JSON}}", tracksJson)
            .Replace("{{DESCRIPTION}}", System.Net.WebUtility.HtmlEncode(share.Description ?? "Shared Music"));
        return Content(html, "text/html; charset=utf-8");
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
        var devices = SubsonicStore.GetDevicesByJellyfinUserId(user.Id.ToString("N"));
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
        var password = GeneratePassword();
        SubsonicStore.UpdateDevicePassword(id, password);
        return Ok(new { password, message = "Password reset. Save this password — it will not be shown again." });
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
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        var shares = SubsonicStore.GetSharesForUser(user.Username);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(shares.Select(s => {
            var secret = SubsonicStore.GetShareSecret(s.ShareUid) ?? "";
            return new {
                uid = s.ShareUid,
                description = s.Description,
                created = s.CreatedAt,
                expires = s.ExpiresAt,
                visitCount = s.VisitCount,
                url = $"{baseUrl}/Subsonic/share/{s.ShareUid}?secret={Uri.EscapeDataString(secret)}",
            };
        }));
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
        var shares = SubsonicStore.GetSharesForUser(user.Username);
        if (!shares.Any(s => s.ShareUid == uid)) return Forbid();
        SubsonicStore.DeleteShare(uid);
        return Ok();
    }

    // ── API: admin share management ──────────────────────────────────────────

    [HttpGet("api/admin/shares")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public IActionResult GetAllSharesAdmin()
    {
        var (user, err) = ResolveUser();
        if (user == null) return err!;
        if (!user.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value)) return Forbid();
        var shares = SubsonicStore.GetAllShares();
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(shares.Select(t => {
            var secret = SubsonicStore.GetShareSecret(t.Share.ShareUid) ?? "";
            return new {
                uid = t.Share.ShareUid, username = t.SubsonicUsername,
                description = t.Share.Description,
                created = t.Share.CreatedAt, expires = t.Share.ExpiresAt,
                visitCount = t.Share.VisitCount,
                url = $"{baseUrl}/Subsonic/share/{t.Share.ShareUid}?secret={Uri.EscapeDataString(secret)}",
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

    // ── Share: M3U download ──────────────────────────────────────────────────

    [HttpGet("share/{uid}/m3u")]
    public IActionResult ShareM3u(string uid)
    {
        var secret = Request.Query["secret"].ToString();
        var share = SubsonicStore.GetShare(uid);
        if (share == null) return NotFound("Share not found.");

        var storedSecret = SubsonicStore.GetShareSecret(uid);
        if (storedSecret != secret) return Unauthorized("Invalid share link.");

        if (!string.IsNullOrEmpty(share.ExpiresAt) && DateTimeOffset.Parse(share.ExpiresAt) < DateTimeOffset.UtcNow)
            return BadRequest("This share has expired.");

        SubsonicStore.IncrementShareVisitCount(uid);

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        foreach (var id in share.EntryIdsFlat)
        {
            if (!Guid.TryParse(id, out var guid)) continue;
            var audio = _library.GetItemById<Audio>(guid);
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
