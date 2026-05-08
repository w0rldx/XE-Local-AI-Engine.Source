namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;

public interface IHubMessageSender
{
    Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task SendInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed, CancellationToken cancellationToken = default);

    Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default);

    Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default);

    Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default);

    Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default);

    Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default);

    Task SendReasoningStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default);

    Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default);

    Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default);

    Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default);

    Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default);
}
