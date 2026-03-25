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
using Jellyfin.Plugin.Subsonic.Store;
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

        var html = GetEmbeddedHtml("share.html")
            .Replace("{{SHARE_UID}}", uid)
            .Replace("{{SECRET}}", System.Net.WebUtility.HtmlEncode(secret));
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

    // ── API: library selection ───────────────────────────────────────────────

    [HttpGet("api/libraries")]
    public IActionResult GetLibraries([FromQuery] string username)
    {
        if (string.IsNullOrEmpty(username)) return BadRequest("username required");

        var devices = SubsonicStore.GetDevicesForUser(username);
        if (devices.Count == 0) return BadRequest("No linked devices for this username.");

        var user = _userManager.GetUserById(Guid.Parse(devices[0].JellyfinUserId));
        if (user == null) return NotFound("Jellyfin user not found.");

        var allFolders = _library.GetVirtualFolders()
            .Where(f => f.CollectionType == MediaBrowser.Model.Entities.CollectionTypeOptions.music)
            .Select(f => new { id = f.ItemId, name = f.Name ?? "" });
        var folders = allFolders;
        var selected = SubsonicStore.GetUserLibrarySettings(username);
        return Ok(new { folders, selected });
    }

    [HttpPost("api/libraries")]
    public IActionResult SetLibraries([FromBody] SetLibrariesRequest req)
    {
        if (string.IsNullOrEmpty(req.Username)) return BadRequest("username required");
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
