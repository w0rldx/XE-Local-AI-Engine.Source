namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Value object carrying approval request payload data.
/// </summary>
public sealed record ApprovalRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string Description { get; init; }
}
