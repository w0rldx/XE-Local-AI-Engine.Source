namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Drains the <see cref="ConversationMaintenanceDispatcher" /> queue one job at a time, each in its own DI scope on
///     the worker's drain token rather than the chat send token.
/// </summary>
/// <remarks>
///     Concurrency is 1 by design: a fold runs on the node's local model, and llama-server serves one sequence per
///     model. Shutdown drains queued jobs inside <see cref="ConversationCompactionOptions.MaintenanceShutdownDrainTimeoutSeconds" />,
///     then cancels the running one and drops the rest; a dropped fold simply runs at the next trigger. Failures log the
///     exception type name only, never conversation content.
/// </remarks>
public sealed class ConversationMaintenanceWorker : BackgroundService
{
    // Bounds the wait for a cancelled job to unwind after the drain window elapsed.
    private static readonly TimeSpan PostDeadlineGrace = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationMaintenanceDispatcher _dispatcher;
    private readonly IOptions<ConversationCompactionOptions> _options;
    private readonly TimeSpan _drainTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationMaintenanceWorker> _logger;

    // Cancelled only when the shutdown drain window elapses, so ordinary operation never interrupts a fold.
    private readonly CancellationTokenSource _drainDeadline = new();

    internal ConversationMaintenanceWorker(IServiceScopeFactory scopeFactory,
        ConversationMaintenanceDispatcher dispatcher,
        IOptions<ConversationCompactionOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationMaintenanceWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _drainTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.MaintenanceShutdownDrainTimeoutSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A host stop completes the writer so the loop drains what is buffered and ends; it reads the DRAIN token.
        await using var stopRegistration = stoppingToken.Register(static state => ((ConversationMaintenanceDispatcher)state!).CompleteWriter(), _dispatcher);

        try
        {
            // Not ReadAllAsync: it hands out buffered jobs without re-checking the token, so a job queued behind the one
            // the deadline interrupted would start on a cancelled token instead of staying queued for StopAsync to drop.
            var reader = _dispatcher.Reader;
            while (await reader.WaitToReadAsync(_drainDeadline.Token))
            {
                _drainDeadline.Token.ThrowIfCancellationRequested();
                if (reader.TryRead(out var job))
                {
                    await ProcessJobAsync(job, _drainDeadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The drain window elapsed; StopAsync accounts for what is still buffered.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _dispatcher.CompleteWriter();

        var abandoned = false;
        if (ExecuteTask is { } readLoop)
        {
            try
            {
                await readLoop.WaitAsync(_drainTimeout, _timeProvider, cancellationToken);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                abandoned = !await CancelAfterDeadlineAsync(readLoop);
            }
        }

        // A job that ignored cancellation is abandoned rather than awaited forever by the base class.
        await base.StopAsync(abandoned ? new CancellationToken(canceled: true) : cancellationToken);
    }

    /// <summary>Runs one job in its own scope; never throws, and always releases the job's coalescing slot.</summary>
    internal async Task ProcessJobAsync(ConversationMaintenanceJob job, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var work = job.Kind switch
            {
                ConversationMaintenanceKind.Compact => CompactWhenOverThresholdAsync(scope.ServiceProvider, job, cancellationToken),
                ConversationMaintenanceKind.Distill => DistillWhenDueAsync(scope.ServiceProvider, job, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "Unknown conversation maintenance kind.")
            };
            await work;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Background {Kind} for conversation {ConversationId} was interrupted by shutdown.", job.Kind, job.ConversationId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Background {Kind} failed ({ErrorClass}) for conversation {ConversationId}; the chat is unaffected.",
                job.Kind,
                exception.GetType().Name,
                job.ConversationId);
        }
        finally
        {
            _dispatcher.Complete(job);
        }
    }

    /// <summary>
    ///     Compacts when the history the next turn would replay exceeds <see cref="ConversationCompactionOptions.AutoCompactFraction" />
    ///     of the turn's usable window.
    /// </summary>
    /// <remarks>
    ///     The projection is <see cref="ConversationStepContextBound.Project" /> and the observed correction is applied to
    ///     the threshold exactly as that bound applies it, so the chat and work-session triggers are one arithmetic.
    /// </remarks>
    private async Task CompactWhenOverThresholdAsync(IServiceProvider services, ConversationMaintenanceJob job, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.AutoCompactEnabled)
        {
            _logger.LogDebug("Automatic compaction is disabled; conversation {ConversationId} was not checked.", job.ConversationId);
            return;
        }

        var usableTokens = job.ContextCapacityTokens - job.ReservedOutputTokens;
        if (usableTokens <= 0)
        {
            return;
        }

        var conversation = await services.GetRequiredService<INodeChatPersistenceService>().GetConversationForTurnAsync(job.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        var estimator = services.GetRequiredService<ITokenEstimator>();
        var projected = ConversationStepContextBound.Project(conversation, estimator, job.ModelName);
        var threshold = TokenEstimatorCalibrationStore.ApplyObservedCorrection((int)(usableTokens * options.AutoCompactFraction),
            estimator.ResolveObservedCorrection(job.ModelName));
        if (projected <= threshold)
        {
            _logger.LogDebug("Conversation {ConversationId} projects ~{Projected} replayed token(s), within the auto-compact threshold of {Threshold}.",
                job.ConversationId,
                projected,
                threshold);
            return;
        }

        // Folds on the node's default local model, like the work-session bound: the fold is not the chat's own work.
        var result = await services.GetRequiredService<IConversationCompactionService>().CompactAsync(job.ConversationId, requestedModel: null, cancellationToken);
        _logger.LogInformation(
            "Conversation {ConversationId} projected ~{Projected} replayed token(s) against an auto-compact threshold of {Threshold}; automatic compaction reported {Outcome} after folding {Folded} message(s).",
            job.ConversationId,
            projected,
            threshold,
            result.Outcome,
            result.MessagesFolded);
    }

    /// <summary>
    ///     Distils when the completed messages after the state watermark reach <see cref="ConversationCompactionOptions.DistillEveryMessages" />
    ///     or their estimated tokens reach <see cref="ConversationCompactionOptions.DistillEveryTokens" />.
    /// </summary>
    private async Task DistillWhenDueAsync(IServiceProvider services, ConversationMaintenanceJob job, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.DistillEnabled)
        {
            _logger.LogDebug("Distillation is disabled; conversation {ConversationId} was not checked.", job.ConversationId);
            return;
        }

        var conversation = await services.GetRequiredService<INodeChatPersistenceService>().GetConversationForTurnAsync(job.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        var pending = ConversationStateDistillationService.PendingMessages(conversation, upToAnchorSequence: null);
        // Only the span after the watermark is projected, with the same estimator and turn model as the compact check.
        var tokens = pending.Count == 0
            ? 0
            : services.GetRequiredService<ITokenEstimator>()
                      .EstimateTokens(pending.Select(static pair => new ChatMessage(string.Equals(pair.Message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User,
                                                 pair.Message.Content))
                                             .ToList(),
                          job.ModelName);
        if (pending.Count < options.DistillEveryMessages && tokens < options.DistillEveryTokens)
        {
            _logger.LogDebug("Conversation {ConversationId} has {Pending} undistilled message(s) (~{Tokens} token(s)); distillation is not due.",
                job.ConversationId, pending.Count, tokens);
            return;
        }

        var outcome = await services.GetRequiredService<IConversationStateDistillationService>()
                                    .DistillPendingAsync(job.ConversationId, requestedModel: null, upToAnchorSequence: null, cancellationToken);
        _logger.LogInformation(
            "Conversation {ConversationId} had {Pending} undistilled message(s) (~{Tokens} token(s)); distillation reported {Status} after {Calls} call(s), state now covers sequence {CoversTo}.",
            job.ConversationId,
            pending.Count,
            tokens,
            outcome.Status,
            outcome.Calls,
            outcome.CoversToSequence);
    }

    /// <summary>Cancels the running job after the drain window elapsed; false when it ignored cancellation past the grace.</summary>
    private async Task<bool> CancelAfterDeadlineAsync(Task readLoop)
    {
        await _drainDeadline.CancelAsync();

        var unwound = true;
        try
        {
            await readLoop.WaitAsync(PostDeadlineGrace, _timeProvider, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            unwound = false;
        }

        var dropped = 0;
        while (_dispatcher.Reader.TryRead(out _))
        {
            dropped++;
        }

        _logger.LogWarning("Conversation maintenance worker shutdown drain exceeded {DrainSeconds:F0}s; abandoned {Abandoned} running and dropped {Dropped} queued job(s).",
            _drainTimeout.TotalSeconds,
            unwound ? 0 : 1,
            dropped);
        return unwound;
    }

    public override void Dispose()
    {
        _drainDeadline.Dispose();
        base.Dispose();
    }
}
