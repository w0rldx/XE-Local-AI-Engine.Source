namespace XE_Local_AI_Engine.Client.Models;

public sealed record ApprovalRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string Description { get; init; }
}