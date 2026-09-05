namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IDevelopmentAttemptExecutionSupervisor
{
    bool StartAttempt(Guid attemptId, DevelopmentAttemptRole role);
    bool StartValidation(Guid taskId);
    ValueTask<bool> TryCancelAsync(Guid attemptId);
}

internal sealed class DevelopmentAttemptExecutionSupervisor(
    IServiceScopeFactory scopeFactory,
    IDevelopmentAttemptLiveBroker liveBroker,
    IDevelopmentAttemptLiveEventPublisher livePublisher,
    ILogger<DevelopmentAttemptExecutionSupervisor> logger) : IDevelopmentAttemptExecutionSupervisor, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _attempts = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _validations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IDevelopmentAttemptLiveBroker _liveBroker = liveBroker ?? throw new ArgumentNullException(nameof(liveBroker));
    private readonly IDevelopmentAttemptLiveEventPublisher _livePublisher = livePublisher ?? throw new ArgumentNullException(nameof(livePublisher));
    private readonly ILogger<DevelopmentAttemptExecutionSupervisor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private int _disposed;

    public bool StartAttempt(Guid attemptId, DevelopmentAttemptRole role)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_attempts.TryAdd(attemptId, cancellation))
        {
            cancellation.Dispose();
            return false;
        }

        if (!_liveBroker.Register(attemptId))
        {
            _attempts.TryRemove(attemptId, out _);
            cancellation.Dispose();
            return false;
        }

        _ = DeliverLiveUpdatesObservedAsync(attemptId, _shutdown.Token);
        _ = RunAttemptObservedAsync(attemptId, role, cancellation.Token);
        return true;
    }

    public bool StartValidation(Guid taskId)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_validations.TryAdd(taskId, cancellation))
        {
            cancellation.Dispose();
            return false;
        }

        _ = RunValidationObservedAsync(taskId, cancellation.Token);
        return true;
    }

    public async ValueTask<bool> TryCancelAsync(Guid attemptId)
    {
        if (!_attempts.TryGetValue(attemptId, out var cancellation))
        {
            return false;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var cancellation in _attempts.Values.Concat(_validations.Values))
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        foreach (var cancellation in _attempts.Values.Concat(_validations.Values))
        {
            cancellation.Dispose();
        }

        _attempts.Clear();
        _validations.Clear();
    }

    private async Task RunAttemptObservedAsync(Guid attemptId,
        DevelopmentAttemptRole role,
        CancellationToken cancellationToken)
    {
        // Hoisted out of the try only so the failure paths below can name the task the attempt belonged to. Null
        // there means the failure beat the snapshot read, which is itself worth seeing in the line.
        DevelopmentExecutionSnapshot? execution = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevelopmentStore>();
            execution = await store.GetExecutionSnapshotAsync(attemptId, cancellationToken).ConfigureAwait(false);
            var repository = await scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>()
                                        .ResolveExecutionAsync(execution, cancellationToken)
                                        .ConfigureAwait(false);
            _ = _liveBroker.TryPublish(ToLiveUpdate(execution,
                DevelopmentAttemptLiveUpdateKind.Activity,
                DevelopmentAttemptStatus.Running,
                "Attempt started."));
            switch (role)
            {
                case DevelopmentAttemptRole.Coder:
                    _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                                   .RunAsync(attemptId, repository, cancellationToken)
                                   .ConfigureAwait(false);
                    break;
                case DevelopmentAttemptRole.Reviewer:
                    _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentReviewerAttemptRunner>()
                                   .RunAsync(attemptId, repository, cancellationToken)
                                   .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("The Development attempt role is not executable.");
            }

            var completed = (await store.ListAttemptsAsync(execution.TaskId, CancellationToken.None).ConfigureAwait(false))
                .Single(attempt => attempt.Id == attemptId);

            // The literal words "attempt finished" open the message on purpose: they are what an operator greps the
            // backend stdout for, and an interpolated id between them would break the search. The same phrase is
            // repeated verbatim in both catch blocks below, because a grep that only hits on healthy runs would miss
            // exactly the runs this item exists for — the failing ones. Both runners rethrow after terminalizing, so
            // this statement is reachable only for Succeeded.
            _logger.LogInformation("Development attempt finished: role={Role} attempt={AttemptId} task={TaskId} status={Status}.",
                role,
                attemptId,
                execution.TaskId,
                completed.Status);
            _ = _liveBroker.TryPublish(ToLiveUpdate(execution,
                DevelopmentAttemptLiveUpdateKind.Terminal,
                completed.Status,
                "Attempt finished.",
                completed.InputTokens,
                completed.OutputTokens));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Development attempt finished: role={Role} attempt={AttemptId} task={TaskId} status={Status}.",
                role,
                attemptId,
                execution?.TaskId,
                DevelopmentAttemptStatus.Cancelled);
        }
        catch (Exception exception)
        {
            // Derived exactly as both runners derive the status they terminalize with, rather than assumed to be
            // Failed: a runner's OWN deadline expiring is an OperationCanceledException that does not satisfy the
            // filter above, so calling it Failed here would contradict the attempt row it just wrote. The one
            // terminal status never logged from this method is Interrupted, which no attempt run produces — it is
            // written by DevelopmentStore.ReconcileRunningAttemptsAsync at host startup, for attempts whose process
            // died before any catch here could run.
            _logger.LogError(exception,
                "Development attempt finished: role={Role} attempt={AttemptId} task={TaskId} status={Status}.",
                role,
                attemptId,
                execution?.TaskId,
                exception is OperationCanceledException ? DevelopmentAttemptStatus.Cancelled : DevelopmentAttemptStatus.Failed);
        }
        finally
        {
            _ = _liveBroker.Complete(attemptId);
            if (_attempts.TryRemove(attemptId, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task DeliverLiveUpdatesObservedAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (!_liveBroker.TryGetDeliveryReader(attemptId, out var reader) || reader is null)
        {
            return;
        }

        try
        {
            await foreach (var update in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _livePublisher.PublishAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown owns cancellation of detached live delivery.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Development live delivery for attempt {AttemptId} stopped.", attemptId);
        }
    }

    private static DevelopmentAttemptLiveUpdate ToLiveUpdate(DevelopmentExecutionSnapshot execution,
        DevelopmentAttemptLiveUpdateKind kind,
        DevelopmentAttemptStatus status,
        string activity,
        long? inputTokens = null,
        long? outputTokens = null) =>
        new()
        {
            ProjectId = execution.ProjectId,
            TaskId = execution.TaskId,
            AttemptId = execution.AttemptId,
            Kind = kind,
            Role = execution.AttemptRole,
            Status = status,
            ModelId = execution.ModelId,
            Provider = execution.Provider,
            CurrentActivity = activity,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };

    private async Task RunValidationObservedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var task = await scope.ServiceProvider.GetRequiredService<IDevelopmentStore>()
                                  .GetTaskAsync(taskId, cancellationToken)
                                  .ConfigureAwait(false);
            var repository = await scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>()
                                        .ResolveProjectAsync(task.ProjectId, cancellationToken)
                                        .ConfigureAwait(false);
            var result = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                    .RunAsync(taskId, repository, cancellationToken)
                                    .ConfigureAwait(false);

            // The gate's verdict was computed and returned all along and then discarded here, which is why a live
            // scan of the backend log found zero hits for "Deterministic validation" across three full rounds. The
            // runner's result carries no failure reason of its own, so the failing gate's own complaint stays where
            // it already lives: the ValidationReport artifact and the task's blocked reason.
            _logger.LogInformation("Deterministic validation for task {TaskId} finished: passed={Passed} target={Target}.",
                taskId,
                result.Passed,
                result.TaskStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Development validation for task {TaskId} was cancelled.", taskId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Development validation for task {TaskId} failed.", taskId);
        }
        finally
        {
            if (_validations.TryRemove(taskId, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }
}
