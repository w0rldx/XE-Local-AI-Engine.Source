namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

public interface INodeSettingsStore
{
    Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default);
}
