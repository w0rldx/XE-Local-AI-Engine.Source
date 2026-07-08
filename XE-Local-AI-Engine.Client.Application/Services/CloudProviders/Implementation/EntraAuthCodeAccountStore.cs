namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Encrypted at-rest store for the Entra ID authorization-code sign-in's MSAL home-account-id, mirroring
///     <see cref="EntraTokenCacheStore" />: DataProtection at rest, Windows user-only file security, *nix
///     <c>0600</c>. A dedicated protector purpose and file keep it from colliding with the device-code /
///     interactive-browser <see cref="EntraTokenCacheStore" /> and the API-key-shaped <see cref="CloudCredentialStore" />.
/// </summary>
public sealed class EntraAuthCodeAccountStore : IEntraAuthCodeAccountStore, IDisposable
{
    private const string ProtectorPurpose = "WorkerNode.EntraId.AuthCodeHomeAccountId.v1";
    private const string RecordFileName = "entra-authcode-account.enc";

    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<EntraAuthCodeAccountStore> _logger;
    private readonly IDataProtector _protector;
    private readonly string _recordPath;

    public EntraAuthCodeAccountStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<EntraAuthCodeAccountStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _recordPath = Path.Combine(dataDirectory.Root, RecordFileName);
        _logger = logger;
    }

    public async Task<string?> LoadHomeAccountIdAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_recordPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_recordPath, cancellationToken).ConfigureAwait(false);
                var payload = _protector.Unprotect(protectedPayload);
                var homeAccountId = Encoding.UTF8.GetString(payload);
                return string.IsNullOrWhiteSpace(homeAccountId) ? null : homeAccountId;
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Entra ID authorization-code account record decryption failed. Clearing the stored record.");
                ClearRecordFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Entra ID authorization-code account record could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveHomeAccountIdAsync(string homeAccountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        var protectedPayload = _protector.Protect(Encoding.UTF8.GetBytes(homeAccountId));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_recordPath, protectedPayload, cancellationToken).ConfigureAwait(false);
            ApplyPlatformFileSecurity();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_recordPath))
            {
                File.Delete(_recordPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private void ApplyPlatformFileSecurity()
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsFileSecurity();
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(_recordPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private void ApplyWindowsFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        var fileInfo = new FileInfo(_recordPath);
        fileInfo.SetAccessControl(fileSecurity);
    }

    private void ClearRecordFileBestEffort()
    {
        try
        {
            if (File.Exists(_recordPath))
            {
                File.Delete(_recordPath);
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to delete the Entra ID authorization-code account record file.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Failed to delete the Entra ID authorization-code account record file.");
        }
    }
}
