namespace Jellyfin.Plugin.Subsonic.Response;

/// <summary>Subsonic API protocol version advertised to clients.</summary>
public static class SubsonicConstants
{
    public const string Version = "1.16.1";
    public const string ServerVersion = "0.1.0";
    public const string ServerType = "subfin-plugin";
}

/// <summary>Standard Subsonic error codes.</summary>
public static class ErrorCode
{
    public const int Generic = 0;
    public const int RequiredParameterMissing = 10;
    public const int ClientUpgrade = 20;
    public const int ServerDown = 30;
    public const int WrongCredentials = 40;
    public const int TokenAuthNotSupported = 41;
    public const int NotLicensed = 50;
    public const int TrialExpired = 60;
    public const int NotFound = 70;
}

/// <summary>Builds JSON-serializable Subsonic envelope objects.</summary>
public static class SubsonicEnvelope
{
    public static Dictionary<string, object> Ok(Dictionary<string, object>? payload = null)
    {
        var inner = new Dictionary<string, object>
        {
            ["status"] = "ok",
            ["version"] = SubsonicConstants.Version,
            ["type"] = SubsonicConstants.ServerType,
            ["serverVersion"] = SubsonicConstants.ServerVersion,
            ["openSubsonic"] = true,
        };
        if (payload != null)
            foreach (var kv in payload)
                inner[kv.Key] = kv.Value;

        return new Dictionary<string, object> { ["subsonic-response"] = inner };
    }

    public static Dictionary<string, object> Error(int code, string message)
    {
        return new Dictionary<string, object>
        {
            ["subsonic-response"] = new Dictionary<string, object>
            {
                ["status"] = "failed",
                ["version"] = SubsonicConstants.Version,
                ["error"] = new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            },
        };
    }
}
