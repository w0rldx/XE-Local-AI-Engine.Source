namespace XE_Local_AI_Engine.Client.Models.Encrypted;

using System.Text.Json.Serialization;

public sealed record EncryptedConversationMessageDto
{
    [JsonPropertyOrder(1)]
    public required Guid Id { get; init; }

    [JsonPropertyOrder(2)]
    public required MessageRole Role { get; init; }

    [JsonPropertyOrder(3)]
    public required int SortOrder { get; init; }

    [JsonPropertyOrder(4)]
    public required int EpochVersion { get; init; }

    [JsonPropertyOrder(5)]
    public required string Aad { get; init; }

    [JsonPropertyOrder(6)]
    public required ReadOnlyMemory<byte> NodeWrappedEpochKey { get; init; }

    [JsonPropertyOrder(7)]
    public required ReadOnlyMemory<byte> ClientEphemeralPublicKey { get; init; }

    [JsonPropertyOrder(8)]
    public required ReadOnlyMemory<byte> Ciphertext { get; init; }

    [JsonPropertyOrder(9)]
    public required ReadOnlyMemory<byte> ContentIv { get; init; }
}
