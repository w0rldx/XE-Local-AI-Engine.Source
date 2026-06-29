namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Persistence boundary for cloud credential data.
/// </summary>
public sealed class CloudCredentialStore : ICloudCredentialStore, IDisposable
{
    private const string CredentialsFileName = "cloud-credentials.enc";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _credentialsPath;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<CloudCredentialStore> _logger;
    private readonly IDataProtector _protector;

    public CloudCredentialStore(IDataProtectionProvider dataProtectionProvider,
        INodeDataDirectory dataDirectory,
        ILogger<CloudCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = dataProtectionProvider.CreateProtector("WorkerNode.CloudCredentialStore.v1");
        _credentialsPath = Path.Combine(dataDirectory.Root, CredentialsFileName);
        _logger = logger;
    }

    public async Task<StoredCloudProviderConfig?> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return null;
            }

            byte[] payload;
            try
            {
                var protectedPayload = await File.ReadAllBytesAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
                payload = _protector.Unprotect(protectedPayload);
            }
            catch (CryptographicException exception)
            {
                _logger.LogWarning(exception, "Cloud credential decryption failed. Clearing stored cloud credentials.");
                ClearCredentialsFileBestEffort();
                return null;
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Cloud credentials could not be read from disk.");
                return null;
            }

            // Shape-detect before any destructive catch so a liftable legacy payload is never deleted (HIGH-2).
            try
            {
                return ParseConfigPayload(payload);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Cloud credentials could not be deserialized. Clearing stored cloud credentials.");
                ClearCredentialsFileBestEffort();
                return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveConfigAsync(StoredCloudProviderConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateConfig(config);

        var payload = JsonSerializer.SerializeToUtf8Bytes(config, SerializerOptions);
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

    public async Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var config = await LoadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config?.AzureFoundry is not { } connection)
        {
            return null;
        }

        var firstModel = connection.Models.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model.DeploymentName))
                         ?? (connection.Models.Count > 0 ? connection.Models[0] : null);
        if (firstModel is null)
        {
            return null;
        }

        return new StoredCloudCredentials
        {
            ProviderName = config.ProviderName,
            Endpoint = connection.Endpoint,
            ApiKey = connection.ApiKey ?? string.Empty,
            DeploymentName = firstModel.DeploymentName,
        };
    }

    public async Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var config = new StoredCloudProviderConfig
        {
            SchemaVersion = 2,
            ProviderName = credentials.ProviderName,
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = credentials.Endpoint,
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = credentials.ApiKey,
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = credentials.DeploymentName,
                        DisplayLabel = credentials.DeploymentName,
                    },
                ],
            },
        };

        await SaveConfigAsync(config, cancellationToken).ConfigureAwait(false);
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

    private static StoredCloudProviderConfig? ParseConfigPayload(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Stored cloud provider config root was not a JSON object.");
        }

        // v2 canonical shape: an azureFoundry object or an explicit schemaVersion is present.
        if (HasProperty(root, "azureFoundry") || HasProperty(root, "schemaVersion"))
        {
            return JsonSerializer.Deserialize<StoredCloudProviderConfig>(payload, SerializerOptions)
                   ?? throw new JsonException("Stored cloud provider config could not be deserialized.");
        }

        // Legacy v1 shape: a flat { providerName, endpoint, apiKey, deploymentName } credential blob.
        if (HasProperty(root, "deploymentName") || HasProperty(root, "apiKey") || HasProperty(root, "endpoint"))
        {
            return LiftLegacyV1(root);
        }

        throw new JsonException("Stored cloud provider config shape was not recognized.");
    }

    private static StoredCloudProviderConfig LiftLegacyV1(JsonElement root)
    {
        var providerName = GetString(root, "providerName") ?? CloudProviderOptions.ProviderAzureFoundry;
        var endpoint = GetString(root, "endpoint") ?? string.Empty;
        var apiKey = GetString(root, "apiKey");
        var deploymentName = GetString(root, "deploymentName") ?? string.Empty;

        return new StoredCloudProviderConfig
        {
            SchemaVersion = 2,
            ProviderName = providerName,
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = endpoint,
                AuthMode = AzureFoundryAuthMode.ApiKey,
                ApiKey = apiKey,
                Models =
                [
                    new StoredAzureFoundryModel
                    {
                        DeploymentName = deploymentName,
                        DisplayLabel = deploymentName,
                    },
                ],
            },
        };
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        return element.EnumerateObject()
                      .Any(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static void ValidateConfig(StoredCloudProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ProviderName))
        {
            throw new ArgumentException("Stored cloud provider name must be provided.", nameof(config));
        }

        if (!string.Equals(config.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Stored cloud provider is not supported.", nameof(config));
        }

        if (config.AzureFoundry is not { } connection)
        {
            throw new ArgumentException("Stored cloud provider config must contain an Azure Foundry connection.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(connection.Endpoint))
        {
            throw new ArgumentException("Stored cloud provider endpoint must be provided.", nameof(config));
        }

        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Stored cloud provider endpoint must be an absolute HTTPS URL.", nameof(config));
        }

        if (!AzureFoundryEndpoints.IsAllowedHost(endpoint))
        {
            throw new ArgumentException("Stored cloud provider endpoint host is not an allowed Azure endpoint.", nameof(config));
        }

        if (!connection.Models.Any(model => !string.IsNullOrWhiteSpace(model.DeploymentName)))
        {
            throw new ArgumentException("Stored cloud provider connection must contain at least one model with a deployment name.", nameof(config));
        }

        if (connection.AuthMode == AzureFoundryAuthMode.ApiKey && string.IsNullOrWhiteSpace(connection.ApiKey))
        {
            throw new ArgumentException("Stored cloud provider API key must be provided when the auth mode is API key.", nameof(config));
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
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

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
