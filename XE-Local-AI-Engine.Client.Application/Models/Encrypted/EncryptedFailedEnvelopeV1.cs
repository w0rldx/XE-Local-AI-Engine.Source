namespace XE_Local_AI_Engine.Client.Models.Encrypted;

/// <summary>
///     Value object carrying encrypted failed envelope v1 data.
/// </summary>
public sealed record EncryptedFailedEnvelopeV1
{
    public int ProtocolVersion { get; init; } = 1;

    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required string FailureCategory { get; init; }

    public required string Error { get; init; }
}
