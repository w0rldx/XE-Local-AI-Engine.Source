namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>Outcome of a completed download: the final path, verified size, sha256 (when an OID was exposed), and revision.</summary>
internal sealed class HfDownloadResult
{
    public required string LocalPath { get; init; }

    public required long SizeBytes { get; init; }

    public required string? Sha256 { get; init; }

    public required string ResolvedRevision { get; init; }
}
