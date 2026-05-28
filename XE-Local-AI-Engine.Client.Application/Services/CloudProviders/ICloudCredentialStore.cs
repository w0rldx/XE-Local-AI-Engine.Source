namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

public interface ICloudCredentialStore
{
    Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
