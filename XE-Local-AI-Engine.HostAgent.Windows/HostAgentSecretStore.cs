namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

public sealed class HostAgentSecretStore : IDisposable
{
    private readonly WindowsHostAgentAcl _acl;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HostAgentWindowsPaths _paths;
    private readonly IHostAgentSecretProtector _protector;

    public HostAgentSecretStore(HostAgentWindowsPaths paths,
        IHostAgentSecretProtector protector,
        WindowsHostAgentAcl acl)
    {
        _paths = paths;
        _protector = protector;
        _acl = acl;
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    [SupportedOSPlatform("windows")]
    public async Task<string> GetOrCreateAdminTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("HostAgent.Windows secret storage is only available on Windows.");
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.AdminTokenPath))
            {
                var protectedPayload = await File.ReadAllBytesAsync(_paths.AdminTokenPath, cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(_protector.Unprotect(protectedPayload));
            }

            var token = CreateSecretToken();
            var protectedToken = _protector.Protect(Encoding.UTF8.GetBytes(token));

            _acl.ApplySecretDirectoryAcl(_paths.SecretDirectory);
            await File.WriteAllBytesAsync(_paths.AdminTokenPath, protectedToken, cancellationToken).ConfigureAwait(false);
            _acl.ApplySecretFileAcl(_paths.AdminTokenPath);

            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    public async Task ClearAdminTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_paths.AdminTokenPath))
            {
                File.Delete(_paths.AdminTokenPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string CreateSecretToken()
    {
        Span<byte> tokenBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        return Convert.ToBase64String(tokenBytes);
    }
}
