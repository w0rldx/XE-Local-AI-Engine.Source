namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Single-consumer durable FIFO for training runs — the dataset-generation loop's shape, with the exclusivity
///     acquisitions a run needs and generation does not.
/// </summary>
/// <remarks>
///     <para>
///         Everything exclusive is acquired BEFORE the claim, and released the moment the queue turns out to be empty.
///         Claiming first and then discovering the GPU is busy would leave the work item <c>Running</c> with nothing
///         running it, and the store pins attempt to 1 — there is no retry to fall back on. Acquiring first means a
///         refusal simply leaves the item Queued for the next poll.
///     </para>
///     <para>
///         Three gates, in order: no other GPU work in flight (benchmark, dataset generation, image job), the
///         process-wide <see cref="ITrainingActivity" /> flag, and the llama.cpp runtime-mutation lease — which refuses
///         while any inference process is running, so a run cannot start behind a warm model.
///     </para>
///     <para>
///         <strong>The evaluation branch takes the first two gates and NOT the third.</strong> An evaluation's whole job
///         is to load a model and ask it one question per hold-out sample, and the mutation lease exists to forbid
///         exactly that — it refuses while a model is loaded, and a model load refuses while it is held. So an
///         evaluation holds <see cref="ITrainingActivity" /> (nothing else GPU-bound starts beside it, and it does not
///         start beside anything else) but reaches its model through the ordinary chat path. The queue peeks the head's
///         kind BEFORE claiming, because the exclusivity a kind needs has to be held before the claim: attempt is
///         pinned to 1, so a claim that turned out to need locks the consumer is not holding could not be handed back.
///     </para>
/// </remarks>
public sealed class TrainingRunQueueHostedService(
    IServiceScopeFactory scopeFactory,
    ITrainingRunQueueSignal signal,
    ITrainingRunEventBuffer events,
    ITrainingActivity trainingActivity,
    ILlamaServerProcessSupervisor supervisor,
    IImageJobCoordinator imageJobs,
    IOptions<TrainingRunQueueOptions> options,
    ILogger<TrainingRunQueueHostedService> logger) : BackgroundService
{
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IImageJobCoordinator _imageJobs = imageJobs ?? throw new ArgumentNullException(nameof(imageJobs));
    private readonly ILogger<TrainingRunQueueHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _pollInterval = (options ?? throw new ArgumentNullException(nameof(options))).Value.PollInterval;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ITrainingRunQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
    private readonly ITrainingActivity _trainingActivity = trainingActivity ?? throw new ArgumentNullException(nameof(trainingActivity));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            TrainingWorkClaim? claim = null;
            IDisposable? activity = null;
            ILlamaServerRuntimeMutationLease? lease = null;
            try
            {
                if (!await OtherGpuWorkActiveAsync(stoppingToken).ConfigureAwait(false))
                {
                    activity = _trainingActivity.TryBegin();
                    if (activity is not null)
                    {
                        var kind = await PeekAsync(stoppingToken).ConfigureAwait(false);
                        if (kind == TrainingWorkKind.TrainingRun)
                        {
                            lease = await _supervisor.TryAcquireRuntimeMutationLeaseAsync(stoppingToken).ConfigureAwait(false);
                            if (lease is not null)
                            {
                                claim = await ClaimAsync(TrainingWorkKind.TrainingRun, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        else if (kind == TrainingWorkKind.EvaluationRun)
                        {
                            // No lease: see the class remarks. The activity flag is the exclusivity an evaluation needs.
                            claim = await ClaimAsync(TrainingWorkKind.EvaluationRun, stoppingToken).ConfigureAwait(false);
                        }
                    }
                }

                if (claim is not null)
                {
                    await using var executionScope = _scopeFactory.CreateAsyncScope();
                    if (claim.Kind == TrainingWorkKind.EvaluationRun)
                    {
                        await executionScope.ServiceProvider.GetRequiredService<IEvaluationRunExecutor>()
                                            .ExecuteAsync(claim, stoppingToken)
                                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await executionScope.ServiceProvider.GetRequiredService<ITrainingRunExecutor>()
                                            .ExecuteAsync(claim, stoppingToken)
                                            .ConfigureAwait(false);
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
                    await lease.DisposeAsync().ConfigureAwait(false);
                }

                activity?.Dispose();
            }

            if (claim is null)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     The converse of the exclusivity checks the other queues make. Each is a status query against the store that
    ///     already owns the enum, rather than a fourth "is anything running" registry.
    /// </summary>
    private async Task<bool> OtherGpuWorkActiveAsync(CancellationToken cancellationToken)
    {
        if (_imageJobs.HasActiveJob)
        {
            return true;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var benchmarks = scope.ServiceProvider.GetRequiredService<IBenchmarkStore>();
        if (await benchmarks.HasActiveWorkAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var datasets = scope.ServiceProvider.GetRequiredService<ITrainingDatasetStore>();
        return await datasets.HasActiveGenerationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TrainingWorkKind?> PeekAsync(CancellationToken stoppingToken)
    {
        await using var peekScope = _scopeFactory.CreateAsyncScope();
        var store = peekScope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        return await store.PeekNextKindAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task<TrainingWorkClaim?> ClaimAsync(TrainingWorkKind kind, CancellationToken stoppingToken)
    {
        await using var claimScope = _scopeFactory.CreateAsyncScope();
        var store = claimScope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
        return await store.ClaimNextAsync(kind, stoppingToken).ConfigureAwait(false);
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

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITrainingRunStore>();
            foreach (var runId in await store.RecoverOnStartupAsync(stoppingToken).ConfigureAwait(false))
            {
                // A buffer that survived into this process cannot describe the new run; drop it so a reconnecting
                // client is told to replay rather than shown stale progress from a run that no longer exists.
                _events.EvictPlaintext(runId);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Training run startup recovery failed; the queue continues with unrecovered work items.");
        }
    }
}
