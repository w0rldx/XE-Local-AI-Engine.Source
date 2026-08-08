namespace XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed record EncryptedChunkEnvelopeV1
{
    public const string ContentKind = "content";
    public const string ReasoningKind = "reasoning";

    public int ProtocolVersion { get; init; } = 1;

    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public string Kind { get; init; } = ContentKind;

    public required ReadOnlyMemory<byte> ChunkIv { get; init; }

    public required ReadOnlyMemory<byte> ChunkCiphertext { get; init; }

    public required long Sequence { get; init; }
}
