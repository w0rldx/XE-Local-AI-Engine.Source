namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Client.Configuration;

/// <summary>
///     Persistence boundary for cloud credential data.
/// </summary>
public sealed class CloudCredentialStore : ICloudCredentialStore, IDisposable
{
    private const string CredentialsFileName = "cloud-credentials.enc";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _credentialsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<CloudCredentialStore> _logger;
    private readonly IDataProtector _protector;

    public CloudCredentialStore(IDataProtectionProvider dataProtectionProvider,
        IHostEnvironment hostEnvironment,
        ILogger<CloudCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector("WorkerNode.CloudCredentialStore.v1");
        _credentialsPath = Path.Combine(hostEnvironment.ContentRootPath, CredentialsFileName);
        _logger = logger;
    }

    public async Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
                var payload = _protector.Unprotect(protectedPayload);
                return DeserializeCredentials(payload);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Cloud credential decryption failed. Clearing stored cloud credentials.");
                ClearCredentialsFileBestEffort();
                return null;
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Cloud credentials could not be deserialized. Clearing stored cloud credentials.");
                ClearCredentialsFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Cloud credentials could not be read from disk.");
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ValidateCredentials(credentials);

        var payload = JsonSerializer.SerializeToUtf8Bytes(credentials, SerializerOptions);
        var protectedPayload = _protector.Protect(payload);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(_credentialsPath, protectedPayload, cancellationToken).ConfigureAwait(false);
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
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
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

    private static StoredCloudCredentials DeserializeCredentials(byte[] payload)
    {
        var credentials = JsonSerializer.Deserialize<StoredCloudCredentials>(payload, SerializerOptions);
        return credentials ?? throw new InvalidOperationException("Stored cloud credentials could not be deserialized.");
    }

    private static void ValidateCredentials(StoredCloudCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.ProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.DeploymentName);

        if (!string.Equals(credentials.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Stored cloud credential provider is not supported.", nameof(credentials));
        }

        if (!Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Stored cloud credential endpoint must be an absolute HTTPS URL.", nameof(credentials));
        }
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
            File.SetUnixFileMode(_credentialsPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private void ApplyWindowsFileSecurity()
    {
        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(true, false);

        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is not null)
        {
            fileSecurity.AddAccessRule(new FileSystemAccessRule(currentIdentity.User,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        var fileInfo = new FileInfo(_credentialsPath);
        fileInfo.SetAccessControl(fileSecurity);
    }

    private void ClearCredentialsFileBestEffort()
    {
        try
        {
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete cloud credentials file.");
        }
    }
}
