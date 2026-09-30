namespace XE_Local_AI_Engine.Client.Services.Blobs;

internal sealed class ManagedBlobWriteResult
{
    public required string OpaqueReference { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }
}
