namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for AgentHome selected folders. The host path is encrypted at rest via the node
///     encryption interceptors; reads return it decrypted in <see cref="SelectedFolderRecord.HostPath" />. This store
///     performs no path/alias validation — that is the application-layer resolver's responsibility; the store only
///     enforces the unique-alias backstop.
/// </summary>
public interface INodeSelectedFolderStore
{
    /// <summary>Persists a new selected folder and returns the stored record (host path decrypted).</summary>
    Task<SelectedFolderRecord> AddAsync(string folderAlias, string hostPath, SelectedFolderMode mode, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no folder is registered.</summary>
    Task<SelectedFolderRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="folderAlias" />, or <c>null</c> when no folder uses that alias.</summary>
    Task<SelectedFolderRecord?> GetByAliasAsync(string folderAlias, CancellationToken cancellationToken = default);

    /// <summary>Returns every registered selected folder, oldest first.</summary>
    Task<IReadOnlyList<SelectedFolderRecord>> ListAsync(CancellationToken cancellationToken = default);
}
