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

internal sealed class DevelopmentAttemptExecutionSupervisor : IDevelopmentAttemptExecutionSupervisor, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _attempts = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _validations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDevelopmentAttemptLiveBroker _liveBroker;
    private readonly IDevelopmentAttemptLiveEventPublisher _livePublisher;
    private readonly ILogger<DevelopmentAttemptExecutionSupervisor> _logger;
    private int _disposed;

    public DevelopmentAttemptExecutionSupervisor(
        IServiceScopeFactory scopeFactory,
        IDevelopmentAttemptLiveBroker liveBroker,
        IDevelopmentAttemptLiveEventPublisher livePublisher,
        ILogger<DevelopmentAttemptExecutionSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(liveBroker);
        ArgumentNullException.ThrowIfNull(livePublisher);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _liveBroker = liveBroker;
        _livePublisher = livePublisher;
        _logger = logger;
    }

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

        await cancellation.CancelAsync();
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        foreach (var cancellation in _attempts.Values.Concat(_validations.Values))
        {
            await cancellation.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
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
            execution = await store.GetExecutionSnapshotAsync(attemptId, cancellationToken);
            var repository = await scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>()
                                        .ResolveExecutionAsync(execution, cancellationToken);
            _ = _liveBroker.TryPublish(ToLiveUpdate(execution,
                DevelopmentAttemptLiveUpdateKind.Activity,
                DevelopmentAttemptStatus.Running,
                "Attempt started."));
            switch (role)
            {
                case DevelopmentAttemptRole.Coder:
                    _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentCoderAttemptRunner>()
                                   .RunAsync(attemptId, repository, cancellationToken);
                    break;
                case DevelopmentAttemptRole.Reviewer:
                    _ = await scope.ServiceProvider.GetRequiredService<IDevelopmentReviewerAttemptRunner>()
                                   .RunAsync(attemptId, repository, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException("The Development attempt role is not executable.");
            }

            var completed = (await store.ListAttemptsAsync(execution.TaskId, CancellationToken.None))
                .Single(attempt => attempt.Id == attemptId);

            // "attempt finished" opens the message verbatim because an operator greps backend stdout for it; both
            // catch blocks repeat it so failing runs hit too. Runners rethrow, so this line is reached on Succeeded.
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
            // Status derived as both runners derive it, never assumed Failed: a runner's own deadline is an
            // OperationCanceledException the filter above misses. Only startup reconciliation writes Interrupted.
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
            await foreach (var update in reader.ReadAllAsync(cancellationToken))
            {
                await _livePublisher.PublishAsync(update, cancellationToken);
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
                                  .GetTaskAsync(taskId, cancellationToken);
            var repository = await scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>()
                                        .ResolveProjectAsync(task.ProjectId, cancellationToken);
            var result = await scope.ServiceProvider.GetRequiredService<IDevelopmentValidationRunner>()
                                    .RunAsync(taskId, repository, cancellationToken);

            // The verdict is logged here or a scan of the backend log finds no "Deterministic validation" at all. The
            // result carries no reason of its own; the gate's complaint lives in ValidationReport and blocked_reason.
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
