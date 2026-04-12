namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Connection;

public sealed class MockHubMessageSender : IHubMessageSender
{
    private readonly object _sync = new();
    private Exception? _nextException;

    public List<Guid> AcceptedInvocations { get; } = [];

    public List<TokenStreamChunkPayload> SentChunks { get; } = [];

    public List<ToolCallRequestPayload> SentToolCalls { get; } = [];

    public List<ApprovalRequestPayload> SentApprovals { get; } = [];

    public List<InvocationCompletedPayload> SentCompletions { get; } = [];

    public List<InvocationFailedPayload> SentFailures { get; } = [];

    public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        AcceptedInvocations.Add(invocationId);
        return Task.CompletedTask;
    }

    public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        SentChunks.Add(new TokenStreamChunkPayload
        {
            InvocationId = invocationId,
            Token = token,
            IsComplete = isComplete
        });

        return Task.CompletedTask;
    }

    public Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        SentToolCalls.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        SentApprovals.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        SentCompletions.Add(payload);
        return Task.CompletedTask;
    }

    public Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfScheduled();
        SentFailures.Add(payload);
        return Task.CompletedTask;
    }

    public void ThrowOnNextSend(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            _nextException = exception;
        }
    }

    private void ThrowIfScheduled()
    {
        Exception? exceptionToThrow;

        lock (_sync)
        {
            exceptionToThrow = _nextException;
            _nextException = null;
        }

        if (exceptionToThrow is not null)
        {
            throw exceptionToThrow;
        }
    }
}
