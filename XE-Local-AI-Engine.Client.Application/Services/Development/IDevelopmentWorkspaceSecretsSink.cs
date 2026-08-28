namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Where <see cref="DevelopmentWorkspaceProvider" /> reports the committed credentials a prepared workspace carries.
///     <para>
///         It exists because that report was the ONE thing in <c>PrepareAsync</c> that is not a value bag: it wrote
///         through <c>IDevelopmentStore.RecordWorkspaceSecretsAsync</c>, which resolves the project from a
///         <c>DevelopmentTask</c> row before it does anything else and then asserts a matching
///         <c>DevelopmentAttempt</c>. A caller preparing a workspace for something that is not a Dev Mode task — a
///         development-workflow node-run — names neither row and never gets past that resolve. Everything else in the
///         method already works from a synthesized snapshot.
///     </para>
///     <para>
///         The keys are named for what they are rather than for the rows the default implementation happens to resolve:
///         the isolation key is whatever the workspace directory is partitioned by (the task, or the node-run), and the
///         attempt key is what makes a repeated preparation of the same workspace idempotent.
///     </para>
/// </summary>
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
internal sealed class DevelopmentStoreWorkspaceSecretsSink(IDevelopmentStore store) : IDevelopmentWorkspaceSecretsSink
{
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task RecordAsync(Guid isolationKey,
        Guid attemptKey,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default) =>
        _store.RecordWorkspaceSecretsAsync(isolationKey, attemptKey, repositoryRelativePaths, cancellationToken);
}
