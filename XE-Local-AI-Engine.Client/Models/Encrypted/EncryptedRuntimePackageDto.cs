namespace XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed record EncryptedRuntimePackageDto
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required ReadOnlyMemory<byte> NodeWrappedEpochKey { get; init; }

    public required ReadOnlyMemory<byte> ClientEphemeralPublicKey { get; init; }

    public required ReadOnlyMemory<byte> Ciphertext { get; init; }

    public required ReadOnlyMemory<byte> ContentIv { get; init; }

    public required ReadOnlyMemory<byte> Aad { get; init; }

    public Guid? InvocationId { get; init; }

    public Guid? ClientNodeId { get; init; }
}
