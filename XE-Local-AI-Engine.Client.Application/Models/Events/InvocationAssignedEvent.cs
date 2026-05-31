namespace XE_Local_AI_Engine.Client.Models.Events;

using XE_Local_AI_Engine.Client.Models.Encrypted;

/// <summary>
///     Value object carrying invocation assigned event data.
/// </summary>
public sealed record InvocationAssignedEvent
{
    public required EncryptedRuntimePackageDto RuntimePackage { get; init; }
}
