namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>The single-consumer benchmark work queue: claim, active-work count, startup recovery. One role of <c>BenchmarkStore</c>.</summary>
public interface IBenchmarkQueueStore
{
    /// <summary>
    ///     The project's ACTIVE work — queued or running — counted per kind, empty when the project is idle.
    /// </summary>
    /// <remarks>
    ///     A run's <see cref="BenchmarkRunRecord.PrimaryStatus" /> does not answer this: a judged, measured or
    ///     pairwise-compared matrix keeps the single-consumer queue and the GPU busy long after every run of it reads
    ///     <see cref="BenchmarkPrimaryStatus.Succeeded" />.
    /// </remarks>
    Task<IReadOnlyDictionary<BenchmarkWorkKind, int>> CountActiveWorkAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default);

    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);

    async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        _ = await RecoverOnStartupAsync(cancellationToken);
        return [];
    }
}
