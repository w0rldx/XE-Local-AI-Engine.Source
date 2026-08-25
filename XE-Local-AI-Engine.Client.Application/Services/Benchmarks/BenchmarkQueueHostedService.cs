namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;

/// <summary>
///     Single-consumer durable FIFO for benchmark runs. A shared <see cref="IGpuWorkGate" /> hold is taken BEFORE the
///     claim and released only when the work is done, so an exclusive holder (a training run, an evaluation, an export)
///     can never admit beside a benchmark that is already executing.
/// </summary>
public sealed class BenchmarkQueueHostedService(
    IServiceScopeFactory scopeFactory,
    IBenchmarkQueueSignal signal,
    IBenchmarkEventBuffer events,
    IGpuWorkGate gpuWorkGate,
    IOptions<BenchmarkQueueOptions> options,
    ILogger<BenchmarkQueueHostedService> logger) : BackgroundService
{
    private readonly TimeSpan _pollInterval = options?.Value.PollInterval > TimeSpan.Zero
        ? options.Value.PollInterval
        : throw new InvalidOperationException("Benchmark queue poll interval must be positive.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            BenchmarkClaimedWork? work = null;
            // The gate is taken BEFORE the claim and held through execution. Refusing at the CLAIM rather than at the
            // executor keeps queued benchmark work queued: it resumes on the next poll once the exclusive holder
            // releases, instead of being terminalized as failed with no retry to fall back on — attempt pins to 1.
            var admission = gpuWorkGate.TryBeginShared(GpuWorkKind.Benchmark);
            try
            {
                if (admission is not null)
                {
                    await using var claimScope = scopeFactory.CreateAsyncScope();
                    var store = claimScope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
                    work = await store.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
                }

                if (work is not null)
                {
                    await using var executionScope = scopeFactory.CreateAsyncScope();
                    try
                    {
                        switch (work.Kind)
                        {
                            case BenchmarkWorkKind.Primary:
                                await executionScope.ServiceProvider.GetRequiredService<IBenchmarkRunExecutor>()
                                                    .ExecuteAsync(work, stoppingToken)
                                                    .ConfigureAwait(false);
                                break;
                            case BenchmarkWorkKind.Judge:
                                await executionScope.ServiceProvider.GetRequiredService<IBenchmarkJudgeExecutor>()
                                                    .ExecuteAsync(work, stoppingToken)
                                                    .ConfigureAwait(false);
                                break;
                            default:
                                await TerminalizeUnsupportedAsync(executionScope.ServiceProvider, work, stoppingToken).ConfigureAwait(false);
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        // Executors own durable terminalization. Reaching this guard means their failure handling itself
                        // failed; keep the single consumer alive so later durable work is not starved.
                        logger.LogError(exception, "Benchmark queue failed while executing {Kind} work for run {RunId}.", work.Kind, work.RunId);
                    }
                }
            }
            finally
            {
                admission?.Dispose();
            }

            if (work is null)
            {
                await signal.WaitAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     A work kind this build has no executor for. It is terminalized as failed rather than left claimed: a
    ///     Running item nothing will ever finish stalls the single-consumer queue behind it forever, and an item that
    ///     silently succeeded would publish a measurement nothing took.
    ///     <para>
    ///         follow-up: the Comparison arm is the pairwise executor's slot (P2 S3). Until it exists, reaching here
    ///         is a real state — a database written by a newer build, or a Comparison item enqueued by a partially
    ///         applied feature — so it fails closed with a reason an operator can act on.
    ///     </para>
    /// </summary>
    private async Task TerminalizeUnsupportedAsync(IServiceProvider services, BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        var reason = $"Benchmark work of kind {work.Kind} is not supported by this build.";
        logger.LogError("Benchmark queue claimed unsupported {Kind} work for run {RunId}; failing it closed.", work.Kind, work.RunId);
        var store = services.GetRequiredService<IBenchmarkStore>();
        if (work.Kind == BenchmarkWorkKind.Comparison)
        {
            await store.MarkComparisonFailedAsync(work.QueueSequence, work.Version, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await store.MarkFidelityFailedAsync(work.RunId, work.Version, reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
        var recovered = await store.RecoverRunsOnStartupAsync(cancellationToken).ConfigureAwait(false);
        foreach (var run in recovered)
        {
            events.EvictPlaintext(run.Id);
        }

        logger.LogInformation("Recovered {RunCount} interrupted benchmark runs.", recovered.Count);
    }
}
