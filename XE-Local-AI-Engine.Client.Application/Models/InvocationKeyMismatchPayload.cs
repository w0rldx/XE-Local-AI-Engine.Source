namespace XE_Local_AI_Engine.Client.Models;

public sealed record InvocationKeyMismatchPayload
{
    public required Guid MessageId { get; init; }

    public required string Reason { get; init; }

    public required string NodeKeyIdUsed { get; init; }
}
