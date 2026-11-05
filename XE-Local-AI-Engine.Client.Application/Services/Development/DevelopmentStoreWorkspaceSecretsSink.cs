namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Dev Mode's sink: the task-scoped store write, forwarded verbatim
///     so a Development attempt records exactly that event.
/// </summary>
internal sealed class DevelopmentStoreWorkspaceSecretsSink : IDevelopmentWorkspaceSecretsSink
{
    private readonly IDevelopmentStore _store;

    public DevelopmentStoreWorkspaceSecretsSink(IDevelopmentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task RecordAsync(Guid isolationKey,
        Guid attemptKey,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default) =>
        _store.RecordWorkspaceSecretsAsync(isolationKey, attemptKey, repositoryRelativePaths, cancellationToken);
}
