using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Configuration;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic;

/// <summary>Jellyfin plugin entry point.</summary>
public class SubsonicPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<SubsonicPlugin> _logger;

    public SubsonicPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<SubsonicPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        _logger = logger;
        Instance = this;

        // Auto-generate salt on first run.
        if (string.IsNullOrEmpty(Configuration.Salt))
        {
            Configuration.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
            _logger.LogInformation("[Subsonic] Generated new encryption salt");
        }

        // Initialize SQLite store.
        var dataDir = Path.Combine(applicationPaths.DataPath, "SubsonicPlugin");
        Directory.CreateDirectory(dataDir);
        SubsonicStore.Initialize(Path.Combine(dataDir, "subsonic.db"), Configuration.Salt);

        _logger.LogInformation("[Subsonic] Plugin loaded, DB at {DataDir}", dataDir);
    }

    public static SubsonicPlugin? Instance { get; private set; }

    public override string Name => "Subsonic";

    public override Guid Id => Guid.Parse("4a3b2c1d-e5f6-7890-abcd-ef1234567890");

    public override string Description =>
        "OpenSubsonic REST API compatibility layer for Subsonic/Navidrome clients.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Subsonic",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.Views.index.html"
            },
            new PluginPageInfo
            {
                Name = "SubsonicAdmin",
                DisplayName = "Subsonic",
                EmbeddedResourcePath = $"{GetType().Namespace}.Web.Views.config.html",
                EnableInMainMenu = true,
            }
        };
    }
}
