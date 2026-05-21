namespace XE_Local_AI_Engine.Tray;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

internal sealed class HostAgentAdminTokenStore
{
    private static readonly byte[] WindowsEntropy = Encoding.UTF8.GetBytes("XE-Local-AI-Engine.HostAgent.Windows.SecretStore.v1");

    private string? _cachedToken;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedToken))
        {
            return _cachedToken;
        }

        _cachedToken = OperatingSystem.IsWindows()
            ? await ReadWindowsTokenAsync(cancellationToken).ConfigureAwait(false)
            : await ReadLinuxTokenAsync(cancellationToken).ConfigureAwait(false);

        return _cachedToken;
    }

    public void ClearCache()
    {
        _cachedToken = null;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> ReadWindowsTokenAsync(CancellationToken cancellationToken)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            throw new InvalidOperationException("Windows common application data path is unavailable.");
        }

        var tokenPath = Path.Combine(programData, "XE-Local-AI-Engine", "host-agent", "secrets", "admin-token.dpapi");
        var protectedPayload = await File.ReadAllBytesAsync(tokenPath, cancellationToken).ConfigureAwait(false);
        var tokenPayload = ProtectedData.Unprotect(protectedPayload, WindowsEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(tokenPayload);
    }

    private static async Task<string> ReadLinuxTokenAsync(CancellationToken cancellationToken)
    {
        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
        {
            throw new InvalidOperationException("XDG_RUNTIME_DIR is required to locate the HostAgent admin token.");
        }

        var tokenPath = Path.Combine(xdgRuntimeDirectory, "xe-host-agent", "admin-token");
        return (await File.ReadAllTextAsync(tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
    }
}
