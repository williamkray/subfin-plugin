using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>URL helpers that respect Jellyfin's BaseUrl.</summary>
internal static class Urls
{
    /// <summary>
    /// Client-facing origin + Jellyfin BaseUrl (e.g. "https://media.example.com/jellyfin"),
    /// for links handed to clients (share URLs, image URLs). Jellyfin mounts its app under
    /// BaseUrl, so ASP.NET exposes it as Request.PathBase.
    /// </summary>
    public static string PublicBase(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>BaseUrl path only ("" or e.g. "/jellyfin"), for same-origin links and redirects.</summary>
    public static string BasePath(HttpRequest request) => request.PathBase.ToString();
}
