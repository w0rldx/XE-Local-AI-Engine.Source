namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Records what a prepared workspace reported about its committed credentials.
/// </summary>
/// <remarks>
///     Hand-rolled rather than substituted: <see cref="IDevelopmentWorkspaceSecretsSink" /> is internal, and NSubstitute
///     would need the whole application assembly opened to Castle's proxy generator to fake it.
/// </remarks>
internal sealed class RecordingWorkspaceSecretsSink : IDevelopmentWorkspaceSecretsSink
{
    private readonly List<(Guid IsolationKey, Guid AttemptKey, IReadOnlyList<string> Paths)> _recorded = [];

    public IReadOnlyList<(Guid IsolationKey, Guid AttemptKey, IReadOnlyList<string> Paths)> Recorded => _recorded;

    public Task RecordAsync(Guid isolationKey,
        Guid attemptKey,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default)
    {
        _recorded.Add((isolationKey, attemptKey, repositoryRelativePaths));
        return Task.CompletedTask;
    }
}
