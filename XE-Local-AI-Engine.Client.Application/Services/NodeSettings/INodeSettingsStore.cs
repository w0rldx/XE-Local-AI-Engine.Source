namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Persistence boundary for i node settings data.
/// </summary>
public interface INodeSettingsStore
{
    Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Synchronous load of the stored settings. Used only on the composition/startup path (DI factory seeds and
    ///     singleton constructors) where blocking on the async file read would starve the thread pool during host
    ///     startup. The settings come from a tiny local JSON file, so a synchronous read is fast and safe; the common
    ///     request-time read still uses <see cref="LoadAsync" />.
    /// </summary>
    StoredNodeSettings Load(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default);
}
