namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Where <see cref="DevelopmentWorkspaceProvider" /> reports the committed credentials a prepared workspace
///     carries.
/// </summary>
/// <remarks>
///     It is a seam because <c>IDevelopmentStore.RecordWorkspaceSecretsAsync</c> resolves the project from a
///     <c>DevelopmentTask</c> row and then asserts a matching <c>DevelopmentAttempt</c>, so a caller preparing a
///     workspace for something that is not a Dev Mode task — a development-workflow node-run — names neither row and
///     never gets past that resolve. The keys are named for what they are: the isolation key is whatever the
///     workspace directory is partitioned by, and the attempt key is what makes a repeated preparation idempotent.
/// </remarks>
internal interface IDevelopmentWorkspaceSecretsSink
{
    Task RecordAsync(Guid isolationKey,
        Guid attemptKey,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Dev Mode's sink: the task-scoped store write the provider did inline before the seam existed, forwarded verbatim
///     so a Development attempt records exactly the event it always did.
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
