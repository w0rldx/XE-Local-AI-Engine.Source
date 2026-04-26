namespace XE_Local_AI_Engine.Client.Testing;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Connection;

public sealed class RecordingHubMessageSender : IHubMessageSender
{
    private readonly IHubMessageSender _inner;
    private readonly IOutboundEventRecorder _recorder;
    private long _sequenceNumber;

    public RecordingHubMessageSender(IHubMessageSender inner, IOutboundEventRecorder recorder)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    public async Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await RecordAsync(nameof(SendPurgeConversationAsync), new { conversationId }, cancellationToken).ConfigureAwait(false);
        await _inner.SendPurgeConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed, CancellationToken cancellationToken = default)
    {
        await RecordAsync(nameof(SendInvocationKeyMismatchAsync), new { messageId, reason, nodeKeyIdUsed }, cancellationToken).ConfigureAwait(false);
        await _inner.SendInvocationKeyMismatchAsync(messageId, reason, nodeKeyIdUsed, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
    {
        await RecordAsync(nameof(SendInvocationAcceptedAsync), new { invocationId }, cancellationToken).ConfigureAwait(false);
        await _inner.SendInvocationAcceptedAsync(invocationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendEncryptedChunkAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendEncryptedChunkAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendEncryptedCompletedAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendEncryptedCompletedAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendEncryptedFailedAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendEncryptedFailedAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default)
    {
        await RecordAsync(nameof(SendTokenStreamChunkAsync), new { invocationId, token, isComplete }, cancellationToken).ConfigureAwait(false);
        await _inner.SendTokenStreamChunkAsync(invocationId, token, isComplete, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendToolCallRequestAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendToolCallRequestAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendApprovalRequestAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendApprovalRequestAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendInvocationCompletedAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendInvocationCompletedAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await RecordAsync(nameof(SendInvocationFailedAsync), payload, cancellationToken).ConfigureAwait(false);
        await _inner.SendInvocationFailedAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private Task RecordAsync(string method, object? payload, CancellationToken cancellationToken)
    {
        var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
        return _recorder.RecordAsync(method, payload, sequenceNumber, cancellationToken);
    }
}
