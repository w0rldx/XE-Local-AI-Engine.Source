namespace XE_Local_AI_Engine.Client.Services.Blobs;

internal enum ManagedBlobReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

internal sealed class ManagedBlobReadResult
{
    public required ManagedBlobReadStatus Status { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }
}
