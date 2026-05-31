namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Persistence boundary for i node settings data.
/// </summary>
public interface INodeSettingsStore
{
    Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default);
}
