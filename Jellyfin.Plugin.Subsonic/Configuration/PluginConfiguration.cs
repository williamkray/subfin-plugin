using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Subsonic.Configuration;

/// <summary>Plugin configuration (stored in Jellyfin config directory).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// AES-256-GCM key salt (base64). Auto-generated on first run if empty.
    /// Never expose this in the UI or logs.
    /// </summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>Last.fm API key for getArtistInfo/getAlbumInfo. Optional — leave empty to disable.</summary>
    public string LastFmApiKey { get; set; } = string.Empty;

    /// <summary>Log all incoming /rest/* requests at Debug level.</summary>
    public bool LogRestRequests { get; set; } = false;

    /// <summary>Enable the sharing feature (createShare, getShares, share pages). Disable to prevent users from creating or accessing shares.</summary>
    public bool SharingEnabled { get; set; } = true;

    /// <summary>
    /// CORS origins allowed for /rest/* and /subfin/* (comma-separated).
    /// Leave empty to allow all origins (default for local dev).
    /// </summary>
    public string CorsOrigins { get; set; } = string.Empty;
}
