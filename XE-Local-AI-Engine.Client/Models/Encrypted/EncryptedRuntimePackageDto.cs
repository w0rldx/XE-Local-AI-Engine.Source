namespace XE_Local_AI_Engine.Client.Models.Encrypted;

using XE_Local_AI_Engine.Client.Models;

public sealed record EncryptedRuntimePackageDto
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid ClientNodeId { get; init; }

    public required Guid MessageId { get; init; }

    public required int EpochVersion { get; init; }

    public required int AgentDefinitionVersion { get; init; }

    public required string ResolvedSystemPrompt { get; init; }

    public required List<MixedEnvelopeAllowedToolDto> AllowedTools { get; init; }

    public string? ModelProfile { get; init; }

    public required TimeoutSettings Timeouts { get; init; }

    public required string ConfigHash { get; init; }

    public required List<EncryptedConversationMessageDto> ConversationContext { get; init; }

    public required string ConversationContextHash { get; init; }

    public required ReadOnlyMemory<byte> NodeWrappedEpochKey { get; init; }

    public required ReadOnlyMemory<byte> ClientEphemeralPublicKey { get; init; }

    public required ReadOnlyMemory<byte> Ciphertext { get; init; }

    public required ReadOnlyMemory<byte> ContentIv { get; init; }

    public required string Aad { get; init; }
}
