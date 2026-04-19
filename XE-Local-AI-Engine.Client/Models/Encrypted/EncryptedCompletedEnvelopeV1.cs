namespace XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed record EncryptedCompletedEnvelopeV1
{
    public int ProtocolVersion { get; init; } = 1;

    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> FinalIv { get; init; }

    public required ReadOnlyMemory<byte> FinalCiphertext { get; init; }

    public required long TotalSequence { get; init; }

    public required IReadOnlyDictionary<string, long> TokenCounts { get; init; }
}
