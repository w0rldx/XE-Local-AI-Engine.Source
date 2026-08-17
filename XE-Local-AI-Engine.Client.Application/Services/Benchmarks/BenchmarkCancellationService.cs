namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkCancellationService
{
    Task<BenchmarkRunRecord> CancelAsync(Guid runId,
        long expectedRunVersion,
        BenchmarkCancellationTarget target,
        CancellationToken cancellationToken = default);
}

public sealed class BenchmarkCancellationService(
    IBenchmarkStore store,
    IBenchmarkCancellationRegistry registry) : IBenchmarkCancellationService
{
    public async Task<BenchmarkRunRecord> CancelAsync(Guid runId,
        long expectedRunVersion,
        BenchmarkCancellationTarget target,
        CancellationToken cancellationToken = default)
    {
        var current = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        if (target == BenchmarkCancellationTarget.Primary
            && current.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded)
        {
            throw new BenchmarkConflictException("PrimaryAlreadySucceeded");
        }

        // Only a live attempt is cancellable: a run with no judging, or one already terminal, has nothing to stop.
        if (target == BenchmarkCancellationTarget.Judge
            && (current.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded
                || current.Judge?.State is not (BenchmarkRunJudgeStates.Queued or BenchmarkRunJudgeStates.Running)))
        {
            throw new BenchmarkConflictException("JudgeNotCancellable");
        }

        var updated = await store.CancelAsync(runId, expectedRunVersion, cancellationToken).ConfigureAwait(false);
        if (target == BenchmarkCancellationTarget.Primary && updated.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested)
        {
            _ = registry.TryCancel(runId, BenchmarkWorkKind.Primary);
        }
        else if (target == BenchmarkCancellationTarget.Judge && updated.Judge?.State == BenchmarkRunJudgeStates.Cancelled)
        {
            _ = registry.TryCancel(runId, BenchmarkWorkKind.Judge);
        }

        return updated;
    }
}
