namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training;

/// <summary>Single-consumer durable FIFO for benchmark runs.</summary>
/// <remarks>
///     A shared <see cref="IGpuWorkGate" /> hold is taken BEFORE the claim and released only when the work is done, so
///     an exclusive holder (a training run, an evaluation, an export) can never admit beside a benchmark that is
///     already executing.
/// </remarks>
public sealed class BenchmarkQueueHostedService : BackgroundService
{
    private readonly TimeSpan _pollInterval;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBenchmarkQueueSignal _signal;
    private readonly IBenchmarkEventBuffer _events;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly ILogger<BenchmarkQueueHostedService> _logger;

    public BenchmarkQueueHostedService(
        IServiceScopeFactory scopeFactory,
        IBenchmarkQueueSignal signal,
        IBenchmarkEventBuffer events,
        IGpuWorkGate gpuWorkGate,
        IOptions<BenchmarkQueueOptions> options,
        ILogger<BenchmarkQueueHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _events = events;
        _gpuWorkGate = gpuWorkGate;
        _logger = logger;
        _pollInterval = options?.Value.PollInterval > TimeSpan.Zero
            ? options.Value.PollInterval
            : throw new InvalidOperationException("Benchmark queue poll interval must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        var reconciled = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Nothing is CLAIMED until recovery has succeeded once: rows the previous process left Running are terminalized only by recovery,
            // and left orphaned they stall the single consumer behind them for this process's whole lifetime. The poll interval is the backoff.
            if (!recovered)
            {
                recovered = await RecoverAsync(stoppingToken);
                if (!recovered)
                {
                    await _signal.WaitAsync(_pollInterval, stoppingToken);
                    continue;
                }
            }

            // Pairwise reconciliation is BEST-EFFORT and gates nothing: it re-enqueues comparisons a crash left missing, which is a cohort's problem, not this consumer's. Blocking the claim
            // on it would let one persistently failing planner starve every primary, judge and fidelity run for the process's lifetime. Retried on the poll interval until it lands once.
            if (!reconciled)
            {
                reconciled = await ReconcilePairwiseAsync(stoppingToken);
            }

            BenchmarkClaimedWork? work = null;
            // The gate is taken BEFORE the claim and held through execution. Refusing at the CLAIM rather than at the executor keeps queued benchmark work queued:
            // it resumes on the next poll once the exclusive holder releases, instead of being terminalized as failed with no retry to fall back on — attempt pins to 1.
            var admission = _gpuWorkGate.TryBeginShared(GpuWorkKind.Benchmark);
            try
            {
                if (admission is not null)
                {
                    await using var claimScope = _scopeFactory.CreateAsyncScope();
                    var store = claimScope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
                    work = await store.ClaimNextAsync(stoppingToken);
                }

                if (work is not null)
                {
                    await using var executionScope = _scopeFactory.CreateAsyncScope();
                    switch (work.Kind)
                    {
                        case BenchmarkWorkKind.Primary:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkRunExecutor>()
                                                .ExecuteAsync(work, stoppingToken);
                            break;
                        case BenchmarkWorkKind.Judge:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkJudgeExecutor>()
                                                .ExecuteAsync(work, stoppingToken);
                            break;
                        case BenchmarkWorkKind.Fidelity:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkFidelityExecutor>()
                                                .ExecuteAsync(work, stoppingToken);
                            break;
                        case BenchmarkWorkKind.Comparison:
                            await executionScope.ServiceProvider.GetRequiredService<IBenchmarkComparisonExecutor>()
                                                .ExecuteAsync(work, stoppingToken);
                            break;
                        default:
                            await TerminalizeUnsupportedAsync(executionScope.ServiceProvider, work, stoppingToken);
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
                    // The CLAIM failed. An exception escaping here would end ExecuteAsync and, under the default BackgroundServiceExceptionBehavior.StopHost,
                    // take the whole node down over a transient database failure. work stays null, so the poll wait below is already the backoff.
                    _logger.LogError(exception, "Benchmark queue failed while claiming work; retrying after the poll interval.");
                }
                else
                {
                    // Executors own durable terminalization. Reaching this guard means their failure handling itself
                    // failed; keep the single consumer alive so later durable work is not starved.
                    _logger.LogError(exception, "Benchmark queue failed while executing {Kind} work for run {RunId}.", work.Kind, work.RunId);
                }
            }
            finally
            {
                admission?.Dispose();
            }

            if (work is null)
            {
                await _signal.WaitAsync(_pollInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    ///     A work kind this build has no executor for.
    /// </summary>
    /// <remarks>
    ///     Terminalized as failed rather than left claimed: a Running item nothing will ever finish stalls the
    ///     single-consumer queue behind it forever, and an item that silently succeeded would publish a measurement
    ///     nothing took. Every kind this build knows has an arm above, so reaching here means a database written by a
    ///     NEWER build — a real state, failed closed with a reason an operator can act on.
    /// </remarks>
    private async Task TerminalizeUnsupportedAsync(IServiceProvider services, BenchmarkClaimedWork work, CancellationToken cancellationToken)
    {
        var reason = $"Benchmark work of kind {work.Kind} is not supported by this build.";
        _logger.LogError("Benchmark queue claimed unsupported {Kind} work for run {RunId}; failing it closed.", work.Kind, work.RunId);
        var store = services.GetRequiredService<IBenchmarkStore>();
        if (work.Kind == BenchmarkWorkKind.Comparison)
        {
            await store.MarkComparisonFailedAsync(work.QueueSequence, work.Version, reason, cancellationToken);
            return;
        }

        _ = await store.MarkFidelityFailedAsync(work.RunId, work.Version, reason, cancellationToken);
    }

    /// <summary>
    ///     Recovers the work items a previous process left Running, before the first claim.
    /// </summary>
    /// <remarks>
    ///     Guarded like the loop below and like both sibling queues: a throw here ends ExecuteAsync and, under the
    ///     default BackgroundServiceExceptionBehavior.StopHost, takes the node down — a transient database failure at
    ///     startup must cost unrecovered work items, not the host. Answers whether it SUCCEEDED, because a failure
    ///     must not cost the work items either: the caller retries on the poll interval and claims nothing until this
    ///     returns true.
    /// </remarks>
    private async Task<bool> RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
            var recovered = await store.RecoverRunsOnStartupAsync(cancellationToken);
            foreach (var run in recovered)
            {
                _events.EvictPlaintext(run.Id);
            }

            _logger.LogInformation("Recovered {RunCount} interrupted benchmark runs.", recovered.Count);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Benchmark startup recovery failed; retrying after the poll interval before any work is claimed.");
            return false;
        }
    }

    /// <summary>
    ///     Re-enqueues the comparisons a crash left missing: a kill between a primary succeeding and its pairs being
    ///     enqueued leaves a cohort permanently one comparison short, every run in it stuck pending.
    /// </summary>
    /// <remarks>
    ///     Separate from <c>RecoverAsync</c> and gating NOTHING: it is one cohort's problem, and a planner that keeps
    ///     throwing must not starve every primary, judge and fidelity run for the process's lifetime — which is what
    ///     folding it into the claim gate did. Answers whether it landed, so the loop stops retrying it. The planner
    ///     is resolved optionally: a host that composed the queue without one cannot have enqueued pairwise work
    ///     either, so there is nothing to reconcile and nothing to retry.
    /// </remarks>
    private async Task<bool> ReconcilePairwiseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var planner = scope.ServiceProvider.GetService<IBenchmarkPairwisePlanner>();
            if (planner is null)
            {
                _logger.LogWarning("No pairwise planner is registered; skipping pairwise reconciliation on startup.");
                return true;
            }

            await planner.ReconcilePairwiseAsync(cancellationToken);
            _logger.LogInformation("Reconciled missing pairwise benchmark comparisons.");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Pairwise benchmark reconciliation failed; retrying after the poll interval. Other benchmark work is unaffected.");
            return false;
        }
    }
}
