namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>Outcome of a completed download: the final path, verified size, sha256 (when an OID was exposed), and revision.</summary>
internal sealed record HfDownloadResult(string LocalPath, long SizeBytes, string? Sha256, string ResolvedRevision);
