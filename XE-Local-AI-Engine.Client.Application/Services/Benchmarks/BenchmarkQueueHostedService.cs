namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkQueueSignal
{
    void Wake();
    Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken);
}

public sealed class BenchmarkQueueSignal : IBenchmarkQueueSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake is sufficient; coalescing avoids unbounded producer pressure.
        }
    }

    public async Task WaitAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        _ = await _signal.WaitAsync(pollInterval, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() =>
        _signal.Dispose();
}

public sealed class BenchmarkQueueOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

public sealed class BenchmarkQueueHostedService(
    IServiceScopeFactory scopeFactory,
    IBenchmarkQueueSignal signal,
    IBenchmarkEventBuffer events,
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
            BenchmarkClaimedWork? work;
            await using (var claimScope = scopeFactory.CreateAsyncScope())
            {
                var store = claimScope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
                work = await store.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
            }

            if (work is null)
            {
                await signal.WaitAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await using var executionScope = scopeFactory.CreateAsyncScope();
            try
            {
                if (work.Kind == BenchmarkWorkKind.Primary)
                {
                    await executionScope.ServiceProvider.GetRequiredService<IBenchmarkRunExecutor>()
                                        .ExecuteAsync(work, stoppingToken)
                                        .ConfigureAwait(false);
                }
                else
                {
                    await executionScope.ServiceProvider.GetRequiredService<IBenchmarkJudgeExecutor>()
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
                // Executors own durable terminalization. Reaching this guard means their failure handling itself failed;
                // keep the single consumer alive so later durable work is not starved.
                logger.LogError(exception, "Benchmark queue failed while executing {Kind} work for run {RunId}.", work.Kind, work.RunId);
            }
        }
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
