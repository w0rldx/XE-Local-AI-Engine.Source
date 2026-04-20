namespace XE_Local_AI_Engine.Client.Models;

public sealed record WorkerKeyRegisteredPayload
{
    public required Guid KeyId { get; init; }
    public required string PublicKey { get; init; }
    public required string PopSignature { get; init; }
    public required string PopChallenge { get; init; }
}
