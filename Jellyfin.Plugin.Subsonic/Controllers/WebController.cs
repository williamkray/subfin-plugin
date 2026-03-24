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
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Library;
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

    // ── API: device management ───────────────────────────────────────────────

    [HttpGet("api/devices")]
    public IActionResult ListDevices([FromQuery] string username)
    {
        if (string.IsNullOrEmpty(username)) return BadRequest("username required");
        var devices = SubsonicStore.GetDevicesForUser(username);
        return Ok(devices.Select(d => new { d.Id, d.DeviceLabel, d.JellyfinUserId, d.CreatedAt }));
    }

    [HttpPost("api/devices/link")]
    public async Task<IActionResult> LinkDevice([FromBody] LinkDeviceRequest req)
    {
        if (string.IsNullOrEmpty(req.SubsonicUsername) || string.IsNullOrEmpty(req.JellyfinUsername) || string.IsNullOrEmpty(req.JellyfinPassword))
            return BadRequest("Missing required fields");

        var jellyfinUser = await _userManager.AuthenticateUser(req.JellyfinUsername, req.JellyfinPassword, req.JellyfinPassword, "127.0.0.1", true);
        if (jellyfinUser == null) return Unauthorized("Invalid Jellyfin credentials.");

        var password = GeneratePassword();
        var deviceId = SubsonicStore.InsertDevice(req.SubsonicUsername, jellyfinUser.Id.ToString("N"), password, req.DeviceLabel ?? "", null, null);

        return Ok(new { deviceId, password, message = "Device linked. Save this password — it will not be shown again." });
    }

    [HttpPost("api/devices/{id}/rename")]
    public IActionResult RenameDevice(long id, [FromBody] RenameRequest req)
    {
        SubsonicStore.UpdateDeviceLabel(id, req.Label ?? "");
        return Ok();
    }

    [HttpPost("api/devices/{id}/reset-password")]
    public IActionResult ResetPassword(long id)
    {
        var password = GeneratePassword();
        SubsonicStore.UpdateDevicePassword(id, password);
        return Ok(new { password, message = "Password reset. Save this password — it will not be shown again." });
    }

    [HttpDelete("api/devices/{id}")]
    public IActionResult DeleteDevice(long id)
    {
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

public record LinkDeviceRequest(string SubsonicUsername, string JellyfinUsername, string JellyfinPassword, string? DeviceLabel);
public record RenameRequest(string? Label);
public record QuickConnectStartRequest(string SubsonicUsername);
public record QuickConnectCompleteRequest(string Secret, string SubsonicUsername, string? DeviceLabel);
public record SetLibrariesRequest(string Username, List<string>? SelectedIds);
