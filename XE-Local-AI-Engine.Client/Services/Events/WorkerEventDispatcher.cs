namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;
    private readonly object _syncRoot = new();

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        ILogger<WorkerEventDispatcher> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public InvocationState? CurrentInvocation { get; private set; }

    public async Task DispatchInvocationAssignedAsync(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                _logger.LogWarning("Ignoring invocation assignment for {InvocationId} because invocation {CurrentInvocationId} is still active.",
                    package.InvocationId,
                    CurrentInvocation!.InvocationId);

                return;
            }

            CurrentInvocation = new InvocationState
            {
                InvocationId = package.InvocationId,
                ConversationId = package.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                ModelUsed = package.ModelProfile
            };

            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Dispatched invocation assignment for {InvocationId}.", package.InvocationId);
        PublishStateChanged(snapshot);

        await RunInvocationAsync(package).ConfigureAwait(false);
    }

    public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received tool call result for request {RequestId}.", evt.RequestId);
        _invocationRunner.ResolveToolCallResult(evt);
        return Task.CompletedTask;
    }

    public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received disconnect request: {Reason}", evt.Reason);
        _invocationRunner.CancelAll();

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                CurrentInvocation!.Status = InvocationStatus.Cancelled;
                CurrentInvocation.Error = evt.Reason;
                snapshot = Clone(CurrentInvocation);
            }
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }

        return Task.CompletedTask;
    }

    public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received approval resolution for request {RequestId}. Approved: {Approved}",
            evt.RequestId,
            evt.Approved);

        return Task.CompletedTask;
    }

    public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _invocationRunner.Cancel(evt.InvocationId);

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (CurrentInvocation?.InvocationId != evt.InvocationId)
            {
                _logger.LogDebug("Ignoring cancellation for {InvocationId} because it does not match the current invocation.",
                    evt.InvocationId);

                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Cancelled;
            CurrentInvocation.Error = evt.Reason;
            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Invocation {InvocationId} marked as cancelled.", evt.InvocationId);
        PublishStateChanged(snapshot);
        return Task.CompletedTask;
    }

    private static InvocationState Clone(InvocationState state)
    {
        return new InvocationState
        {
            InvocationId = state.InvocationId,
            ConversationId = state.ConversationId,
            Status = state.Status,
            StreamedContent = state.StreamedContent,
            StartedAt = state.StartedAt,
            Error = state.Error,
            ModelUsed = state.ModelUsed
        };
    }

    private static bool IsInvocationActive(InvocationState? state)
    {
        return state is not null && state.Status is InvocationStatus.Assigned or InvocationStatus.Running;
    }

    private async Task RunInvocationAsync(RuntimePackage package)
    {
        UpdateInvocation(package.InvocationId,
            static state =>
            {
                state.Status = InvocationStatus.Running;
                return state;
            });

        try
        {
            await _invocationRunner.RunAsync(package).ConfigureAwait(false);

            UpdateInvocation(package.InvocationId,
                static state =>
                {
                    if (state.Status is InvocationStatus.Assigned or InvocationStatus.Running)
                    {
                        state.Status = InvocationStatus.Completed;
                    }

                    return state;
                });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invocation {InvocationId} failed before execution completed.", package.InvocationId);

            UpdateInvocation(package.InvocationId,
                state =>
                {
                    state.Status = InvocationStatus.Failed;
                    state.Error = exception.Message;
                    return state;
                });
        }
    }

    private void UpdateInvocation(Guid invocationId, Func<InvocationState, InvocationState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (CurrentInvocation?.InvocationId != invocationId)
            {
                return;
            }

            CurrentInvocation = update(CurrentInvocation);
            snapshot = Clone(CurrentInvocation);
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }
    }

    private void PublishStateChanged(InvocationState state)
    {
        Volatile.Read(ref InvocationStateChanged)?.Invoke(this, new InvocationStateChangedEventArgs(Clone(state)));
    }
}
