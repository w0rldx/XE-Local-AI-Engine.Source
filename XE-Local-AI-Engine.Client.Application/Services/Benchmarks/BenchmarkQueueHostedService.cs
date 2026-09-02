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
                        case BenchmarkWorkKind.Fidelity:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkFidelityExecutor>()
                                                .ExecuteAsync(work, stoppingToken)
                                                .ConfigureAwait(false);
                            break;
                        case BenchmarkWorkKind.Comparison:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkComparisonExecutor>()
                                                .ExecuteAsync(work, stoppingToken)
                                                .ConfigureAwait(false);
                            break;
                        default:
                            await TerminalizeUnsupportedAsync(executionScope.ServiceProvider, work, stoppingToken).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (work is null)
                {
                    // The CLAIM failed. An exception escaping here would end ExecuteAsync and, under the default
                    // BackgroundServiceExceptionBehavior.StopHost, take the whole node down over a transient database
                    // failure. work stays null, so the poll wait below is already the backoff.
                    logger.LogError(exception, "Benchmark queue failed while claiming work; retrying after the poll interval.");
                }
                else
                {
                    // Executors own durable terminalization. Reaching this guard means their failure handling itself
                    // failed; keep the single consumer alive so later durable work is not starved.
                    logger.LogError(exception, "Benchmark queue failed while executing {Kind} work for run {RunId}.", work.Kind, work.RunId);
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
    ///         Every kind this build knows has an arm above, so reaching here means a database written by a NEWER
    ///         build. That is a real state, and it fails closed with a reason an operator can act on.
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

    /// <summary>
    ///     Guarded like the loop below and like both sibling queues: this runs BEFORE the first claim, so a throw here
    ///     ends ExecuteAsync and, under the default BackgroundServiceExceptionBehavior.StopHost, takes the node down —
    ///     a transient database failure at startup must cost unrecovered work items, not the host.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
            var recovered = await store.RecoverRunsOnStartupAsync(cancellationToken).ConfigureAwait(false);
            foreach (var run in recovered)
            {
                events.EvictPlaintext(run.Id);
            }

            logger.LogInformation("Recovered {RunCount} interrupted benchmark runs.", recovered.Count);

            // The sweep above terminalizes what the kill interrupted; this re-enqueues what it left missing. A crash
            // between a primary succeeding and its pairs being enqueued would otherwise leave a cohort permanently one
            // comparison short, with every run in it stuck pending and nothing that would ever notice.
            //
            // Resolved optionally, unlike the executors in the loop: this runs BEFORE the first claim, so a host that
            // composed the queue without a planner would die here at startup and starve EVERY kind of benchmark work
            // over a leg that had nothing to do — a host with no planner cannot have enqueued pairwise work either.
            var planner = scope.ServiceProvider.GetService<IBenchmarkPairwisePlanner>();
            if (planner is null)
            {
                logger.LogWarning("No pairwise planner is registered; skipping pairwise reconciliation on startup.");
                return;
            }

            await planner.ReconcilePairwiseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Benchmark startup recovery failed; the queue continues with unrecovered work items.");
        }
    }
}
