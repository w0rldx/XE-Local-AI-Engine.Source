namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using System.Security.Cryptography;

/// <summary>
///     Persistence boundary for host agent admin token data.
/// </summary>
public sealed class HostAgentAdminTokenStore
{
    private string? _cachedToken;

    public async Task<string> GetOrCreateAdminTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken))
        {
            return _cachedToken;
        }

        var tokenPath = ResolveTokenPath();
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);

        if (File.Exists(tokenPath))
        {
            _cachedToken = (await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            return _cachedToken;
        }

        _cachedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(tokenPath, _cachedToken, cancellationToken).ConfigureAwait(false);
        HardenTokenFile(tokenPath);
        return _cachedToken;
    }

    private static string ResolveTokenPath()
    {
        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
        {
            throw new InvalidOperationException("XDG_RUNTIME_DIR is required for the HostAgent admin token.");
        }

        return Path.Combine(xdgRuntimeDirectory, "xe-host-agent", "admin-token");
    }

    private static void HardenTokenFile(string tokenPath)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
