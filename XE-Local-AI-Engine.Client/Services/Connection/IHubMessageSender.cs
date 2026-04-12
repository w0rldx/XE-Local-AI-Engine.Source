namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models;

public interface IHubMessageSender
{
    Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default);

    Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default);

    Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default);

    Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default);

    Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default);

    Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default);
}
