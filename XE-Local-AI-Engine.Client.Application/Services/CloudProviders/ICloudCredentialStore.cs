namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Persistence boundary for i cloud credential data.
/// </summary>
public interface ICloudCredentialStore
{
    /// <summary>
    ///     Loads the canonical schema-v2 cloud provider config, lifting a legacy v1 payload in place without
    ///     data loss or file deletion.
    /// </summary>
    Task<StoredCloudProviderConfig?> LoadConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and persists the canonical schema-v2 cloud provider config.
    /// </summary>
    Task SaveConfigAsync(StoredCloudProviderConfig config, CancellationToken cancellationToken = default);

    Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
