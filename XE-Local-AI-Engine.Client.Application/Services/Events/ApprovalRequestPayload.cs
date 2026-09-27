namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed record ApprovalRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string Description { get; init; }
}
