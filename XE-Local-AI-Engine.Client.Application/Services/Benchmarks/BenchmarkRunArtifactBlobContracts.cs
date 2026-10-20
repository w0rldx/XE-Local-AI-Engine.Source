namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public enum BenchmarkRunArtifactBlobReadStatus
{
    Found,
    Missing,
    Tampered,
    SizeMismatch,
    HashMismatch
}

/// <summary>A run artifact's bytes, present only when <see cref="Status" /> is <c>Found</c>.</summary>
public sealed class BenchmarkRunArtifactBlobReadResult
{
    public required BenchmarkRunArtifactBlobReadStatus Status { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }
}
