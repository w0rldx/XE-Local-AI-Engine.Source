namespace XE_Local_AI_Engine.HostAgent.Linux.Security;

public sealed class HostAgentHmacOptions
{
    public const int DefaultBucketSeconds = 15;
    public const int DefaultMaxRequestIdsPerBucket = 1024;

    public string Secret { get; set; } = string.Empty;

    public int BucketSeconds { get; set; } = DefaultBucketSeconds;

    public int MaxRequestIdsPerBucket { get; set; } = DefaultMaxRequestIdsPerBucket;

    public static void Bind(HostAgentHmacOptions options, IConfiguration configuration)
    {
        configuration.GetSection("HostAgent:Hmac").Bind(options);

        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            options.Secret = ReadSecretFromFile(configuration);
        }
    }

    private static string ReadSecretFromFile(IConfiguration configuration)
    {
        var configuredPath = ResolveSecretFilePath(configuration);

        return File.Exists(configuredPath)
            ? File.ReadAllText(configuredPath).Trim()
            : string.Empty;
    }

    public static string ResolveSecretFilePath(IConfiguration configuration)
    {
        return configuration["HostAgent:Hmac:SecretFile"]
               ?? Environment.GetEnvironmentVariable("XE_HOST_AGENT_HMAC_SECRET_FILE")
               ?? ResolveDefaultSecretPath();
    }

    public static bool UsesNativeRuntimeSecretPath(IConfiguration configuration)
    {
        var path = ResolveSecretFilePath(configuration);
        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(xdgRuntimeDirectory)
               && path.StartsWith(Path.Combine(xdgRuntimeDirectory, "xe-host-agent"), StringComparison.Ordinal);
    }

    private static string ResolveDefaultSecretPath()
    {
        if (IsManagedWslRuntime())
        {
            return "/etc/xe-host-agent/hmac-secret";
        }

        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrWhiteSpace(xdgRuntimeDirectory)
            ? "/etc/xe-host-agent/hmac-secret"
            : Path.Combine(xdgRuntimeDirectory, "xe-host-agent", "hmac-secret");
    }

    public static bool IsManagedWslRuntime()
    {
        var configuredMode = Environment.GetEnvironmentVariable("XE_HOST_AGENT_RUNTIME_MODE");
        if (string.Equals(configuredMode, "wsl-managed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string osReleasePath = "/proc/sys/kernel/osrelease";
        if (!File.Exists(osReleasePath))
        {
            return false;
        }

        var release = File.ReadAllText(osReleasePath);
        return release.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
               || release.Contains("wsl", StringComparison.OrdinalIgnoreCase);
    }
}
