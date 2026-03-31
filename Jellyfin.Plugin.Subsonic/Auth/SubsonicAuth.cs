using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>Auth result after resolving Subsonic credentials.</summary>
public record AuthResult(
    string SubsonicUsername,
    string JellyfinUserId,
    string? JellyfinDeviceId,
    string? JellyfinDeviceName,
    /// <summary>Set for share auth. Restrict stream to these track IDs.</summary>
    string? ShareId = null,
    HashSet<string>? ShareAllowedIds = null);

/// <summary>Resolves Subsonic auth params (u/p, u/t/s, apiKey) to a Jellyfin user ID.</summary>
public class SubsonicAuth
{
    private readonly IUserManager _userManager;
    private readonly ILogger<SubsonicAuth> _logger;

    public SubsonicAuth(IUserManager userManager, ILogger<SubsonicAuth> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public record AuthError(int Code, string Message);

    /// <summary>Resolve auth from query params. Returns AuthResult on success, AuthError on failure.</summary>
    public object Resolve(IQueryCollection query)
    {
        var u = query["u"].ToString().Trim();
        var p = query["p"].ToString();
        var t = query["t"].ToString();
        var s = query["s"].ToString();
        var apiKey = query["apiKey"].ToString();

        // Share auth: u=share_<uid>
        if (u.StartsWith("share_", StringComparison.Ordinal))
        {
            var shareUid = u[6..].Trim();
            if (string.IsNullOrEmpty(shareUid))
                return new AuthError(10, "Invalid share username.");

            var rawP = DecodePassword(p);
            if (!string.IsNullOrEmpty(rawP))
            {
                var share = SubsonicStore.GetShare(shareUid);
                if (share != null)
                {
                    var storedSecret = SubsonicStore.GetShareSecret(shareUid);
                    if (storedSecret == rawP)
                    {
                        var device = SubsonicStore.GetDeviceById(share.LinkedDeviceId);
                        if (device != null)
                        {
                            return new AuthResult(
                                device.SubsonicUsername,
                                device.JellyfinUserId,
                                device.JellyfinDeviceId,
                                device.JellyfinDeviceName,
                                ShareId: shareUid,
                                ShareAllowedIds: new HashSet<string>(share.EntryIdsFlat));
                        }
                    }
                }
            }
            return new AuthError(40, "Wrong username or password.");
        }

        if (string.IsNullOrEmpty(u))
            return new AuthError(10, "Required parameter 'u' (username) missing.");

        // Prefer p= / apiKey= over t+s
        string? password = null;
        if (!string.IsNullOrEmpty(p))
        {
            password = DecodePassword(p);
            if (password == null) return new AuthError(40, "Wrong username or password.");
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            password = apiKey;
        }
        else if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(s))
        {
            // Token auth: t = md5(password + s)
            var devices = SubsonicStore.GetDevicePlaintextPasswords(u);
            var hasAny = devices.Count > 0;
            foreach (var (id, label, jellyfinUserId, plain) in devices)
            {
                if (ComputeToken(plain, s) == t)
                {
                    var (devId, devName) = DeviceDisplay(id, label);
                    return new AuthResult(u, jellyfinUserId, devId, devName);
                }
            }
            if (hasAny && devices.TrueForAll(d => string.IsNullOrEmpty(d.PlainPassword)))
                return new AuthError(41, "Token authentication not supported for this account.");
            return new AuthError(40, "Wrong username or password.");
        }

        if (string.IsNullOrEmpty(password))
            return new AuthError(10, "Required parameter 'p', 't'+'s', or 'apiKey' missing.");

        var matched = SubsonicStore.GetDeviceByUsernameAndPassword(u, password);
        if (matched == null)
            return new AuthError(40, "Wrong username or password.");

        var (dId, dName) = DeviceDisplay(matched.Id, matched.DeviceLabel);
        return new AuthResult(u, matched.JellyfinUserId, dId, dName);
    }

    internal static string? DecodePassword(string p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        if (p.StartsWith("enc:", StringComparison.Ordinal))
        {
            try { return Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); }
            catch { return null; }
        }
        return p;
    }

    internal static string ComputeToken(string password, string salt)
    {
        var input = Encoding.UTF8.GetBytes(password + salt);
        return Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
    }

    private static (string Id, string Name) DeviceDisplay(long deviceId, string label) =>
        ("subfin-" + deviceId,
         string.IsNullOrWhiteSpace(label) ? $"Subfin Device {deviceId}" : label);
}
