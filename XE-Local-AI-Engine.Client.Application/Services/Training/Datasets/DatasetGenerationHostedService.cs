namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Single-consumer durable FIFO for dataset generation — the <c>BenchmarkQueueHostedService</c> shape duplicated, not
///     generalized. Startup recovery runs once before the loop: an interrupted <c>Running</c> work item is terminalized
///     as failed (attempt is pinned to 1, so nothing is retried in place) and its replay buffer is evicted.
///     <para>
///         The loop takes a shared <see cref="IGpuWorkGate" /> hold BEFORE it CLAIMS and keeps it until the work is
///         done (decision #13), so an exclusive holder can never admit beside generation that is already executing.
///         Refusing at the claim rather than at the executor keeps queued work queued: it resumes on the next poll once
///         the exclusive holder releases, instead of being terminalized as failed.
///     </para>
/// </summary>
public sealed class DatasetGenerationHostedService(
    IServiceScopeFactory scopeFactory,
    IDatasetGenerationQueueSignal signal,
    IDatasetGenerationEventBuffer events,
    IGpuWorkGate gpuWorkGate,
    IOptions<DatasetGenerationQueueOptions> options,
    ILogger<DatasetGenerationHostedService> logger) : BackgroundService
{
    private readonly IDatasetGenerationEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IGpuWorkGate _gpuWorkGate = gpuWorkGate ?? throw new ArgumentNullException(nameof(gpuWorkGate));
    private readonly ILogger<DatasetGenerationHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _pollInterval = (options ?? throw new ArgumentNullException(nameof(options))).Value.PollInterval;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IDatasetGenerationQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Nothing is CLAIMED until recovery has succeeded once — the benchmark queue's rule, for the benchmark
            // queue's reason: only recovery terminalizes the rows the previous process left Running, and a loop that
            // claims past a failed recovery orphans them for this process's whole lifetime.
            if (!recovered)
            {
                recovered = await RecoverAsync(stoppingToken).ConfigureAwait(false);
                if (!recovered)
                {
                    await WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }
            }

            DatasetGenerationClaimedWork? work = null;
            var admission = _gpuWorkGate.TryBeginShared(GpuWorkKind.DatasetGeneration);
            try
            {
                if (admission is not null)
                {
                    await using var claimScope = _scopeFactory.CreateAsyncScope();
                    var store = claimScope.ServiceProvider.GetRequiredService<ITrainingDatasetStore>();
                    work = await store.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
                }

                if (work is not null)
                {
                    await using var executionScope = _scopeFactory.CreateAsyncScope();
                    await executionScope.ServiceProvider.GetRequiredService<IDatasetGenerationExecutor>()
                                        .ExecuteAsync(work, stoppingToken)
                                        .ConfigureAwait(false);
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
                    _logger.LogError(exception, "The dataset generation queue failed while claiming work; retrying after the poll interval.");
                }
                else
                {
                    // The executor owns durable terminalization. Reaching this guard means its own failure handling
                    // failed; keep the single consumer alive so later durable work is not starved.
                    _logger.LogError(exception, "The dataset generation queue failed while executing dataset {DatasetId}.", work.DatasetId);
                }
            }
            finally
            {
                admission?.Dispose();
            }

            if (work is null)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            _ = await _signal.WaitAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Answers whether recovery SUCCEEDED; the loop claims nothing until it has.</summary>
    private async Task<bool> RecoverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITrainingDatasetStore>();
            var recovered = await store.RecoverOnStartupAsync(stoppingToken).ConfigureAwait(false);
            foreach (var datasetId in recovered)
            {
                // A buffer that survived into this process cannot describe the new run; drop it so a reconnecting client
                // is told to replay rather than shown stale plaintext.
                _events.EvictPlaintext(datasetId);
            }

            _logger.LogInformation("Recovered {DatasetCount} interrupted dataset generations.", recovered.Count);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Dataset generation startup recovery failed; retrying after the poll interval before any work is claimed.");
            return false;
        }
    }
}
