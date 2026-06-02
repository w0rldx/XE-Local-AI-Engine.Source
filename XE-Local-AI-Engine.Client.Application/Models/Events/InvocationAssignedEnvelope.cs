namespace XE_Local_AI_Engine.Client.Models.Events;

using XE_Local_AI_Engine.Client.Models.Encrypted;

public sealed record InvocationAssignedEnvelope
{
    public int Version { get; init; } = 2;

    public required string StorageMode { get; init; }

    public RuntimePackage? Plain { get; init; }

    public EncryptedRuntimePackageDto? Encrypted { get; init; }
}
