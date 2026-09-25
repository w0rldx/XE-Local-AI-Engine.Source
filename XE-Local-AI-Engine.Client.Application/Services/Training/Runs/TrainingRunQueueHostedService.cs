namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Single-consumer durable FIFO for training runs — the dataset-generation loop's shape, with the exclusivity
///     acquisitions a run needs and generation does not.
/// </summary>
/// <remarks>
///     Everything exclusive is acquired BEFORE the claim and released the moment the queue turns out to be empty: claiming
///     first and then discovering the GPU is busy would leave the work item <c>Running</c> with nothing running it, and the
///     store pins attempt to 1, so there is no retry to fall back on — acquiring first simply leaves the item Queued for the
///     next poll. Only work that HOLDS the gate blocks a run; queued-but-unclaimed work does not, because the gate can only
///     speak for work already admitted. Gate order, evaluation branch and kind peek: docs/wiki/18-training.md ("4. Training runs").
/// </remarks>
public sealed class TrainingRunQueueHostedService : BackgroundService
{
    private readonly ITrainingRunEventBuffer _events;
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly ILogger<TrainingRunQueueHostedService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITrainingRunQueueSignal _signal;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private bool _waitingForLease;

    public TrainingRunQueueHostedService(IServiceScopeFactory scopeFactory,
        ITrainingRunQueueSignal signal,
        ITrainingRunEventBuffer events,
        IGpuWorkGate gpuWorkGate,
        ILlamaServerProcessSupervisor supervisor,
        IOptions<TrainingRunQueueOptions> options,
        ILogger<TrainingRunQueueHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        _gpuWorkGate = gpuWorkGate;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _pollInterval = (options ?? throw new ArgumentNullException(nameof(options))).Value.PollInterval;
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        ArgumentNullException.ThrowIfNull(signal);
        _signal = signal;
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Nothing is CLAIMED until recovery has succeeded once — the benchmark queue's rule, for its reason: only
            // recovery terminalizes rows the previous process left Running, and claiming past a failed recovery orphans them.
            if (!recovered)
            {
                recovered = await RecoverAsync(stoppingToken);
                if (!recovered)
                {
                    await WaitAsync(stoppingToken);
                    continue;
                }
            }

            TrainingWorkClaim? claim = null;
            IDisposable? admission = null;
            ILlamaServerRuntimeMutationLease? lease = null;
            try
            {
                // Peek first: the exclusivity a kind needs has to be held before the claim, and an idle queue must not
                // take the gate at all — holding it across the poll would starve every other GPU path on a quiet node.
                var kind = await PeekAsync(stoppingToken);
                if (kind is TrainingWorkKind.TrainingRun or TrainingWorkKind.EvaluationRun)
                {
                    admission = _gpuWorkGate.TryBeginExclusive(kind == TrainingWorkKind.TrainingRun
                        ? GpuWorkKind.TrainingRun
                        : GpuWorkKind.EvaluationRun);
                    if (admission is not null)
                    {
                        if (kind == TrainingWorkKind.TrainingRun)
                        {
                            lease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(stoppingToken);
                            if (lease is not null)
                            {
                                claim = await ClaimAsync(TrainingWorkKind.TrainingRun, stoppingToken);
                            }

                            LogLeaseWait(lease is null);
                        }
                        else
                        {
                            // No lease: see the class remarks. The exclusive hold is the exclusivity an evaluation needs.
                            claim = await ClaimAsync(TrainingWorkKind.EvaluationRun, stoppingToken);
                        }
                    }
                }

                if (claim is not null)
                {
                    await using var executionScope = _scopeFactory.CreateAsyncScope();
                    if (claim.Kind == TrainingWorkKind.EvaluationRun)
                    {
                        await executionScope.ServiceProvider.GetRequiredService<IEvaluationRunExecutor>()
                                            .ExecuteAsync(claim, stoppingToken);
                    }
                    else
                    {
                        await executionScope.ServiceProvider.GetRequiredService<ITrainingRunExecutor>()
                                            .ExecuteAsync(claim, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The executor owns durable terminalization. Reaching this guard means its own failure handling failed;
                // keep the single consumer alive so later durable work is not starved.
                _logger.LogError(exception, "The training run queue failed while executing work item {TargetId}.", claim?.TargetId);
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }

                admission?.Dispose();
            }

            if (claim is null)
            {
                await WaitAsync(stoppingToken);
            }
        }
    }

    /// <summary>
    ///     A queued run behind a warm model waits on the eject-first rule silently otherwise: no status change, no
    ///     reason, no log line — found live. Logged once per transition (waiting → admitted), never per poll.
    /// </summary>
    private void LogLeaseWait(bool waiting)
    {
        if (waiting == _waitingForLease)
        {
            return;
        }

        _waitingForLease = waiting;
        if (waiting)
        {
            _logger.LogInformation("A training run is queued but a model is loaded; it starts once the runtime is idle (eject the loaded model to start it now).");
        }
        else
        {
            _logger.LogInformation("The runtime became idle; the queued training run is being admitted.");
        }
    }

    private async Task<TrainingWorkKind?> PeekAsync(CancellationToken stoppingToken)
    {
        await using var peekScope = _scopeFactory.CreateAsyncScope();
        var store = peekScope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        return await store.PeekNextKindAsync(stoppingToken);
    }

    private async Task<TrainingWorkClaim?> ClaimAsync(TrainingWorkKind kind, CancellationToken stoppingToken)
    {
        await using var claimScope = _scopeFactory.CreateAsyncScope();
        var store = claimScope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        return await store.ClaimNextAsync(kind, stoppingToken);
    }

    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            _ = await _signal.WaitAsync(_pollInterval, stoppingToken);
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
            var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
            var recovered = await store.RecoverOnStartupAsync(stoppingToken);
            foreach (var runId in recovered)
            {
                // A buffer that survived into this process cannot describe the new run; drop it so a reconnecting
                // client is told to replay rather than shown stale progress from a run that no longer exists.
                _events.EvictPlaintext(runId);
            }

            // The startup reaper normally runs recovery first, so a zero here is expected, not "nothing was interrupted".
            _logger.LogInformation("Queue startup recovery marked {RunCount} further interrupted training runs as failed.", recovered.Count);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Training run startup recovery failed; retrying after the poll interval before any work is claimed.");
            return false;
        }
    }
}
