namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Versioned recovery metadata written next to every newly acquired GGUF.</summary>
public sealed record GgufAcquisitionMetadata
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string RegistryRevision { get; init; }
    public required string ModelName { get; init; }
    public required LocalModelOrigin Origin { get; init; }
    public required string LocalFileName { get; init; }
    public required string Quantization { get; init; }
    public required string WeightContentSha256 { get; init; }
    public required long WeightSizeBytes { get; init; }
    public required string WeightMemberFingerprint { get; init; }
    public required string SourceDisplayName { get; init; }
    public required DateTimeOffset AcquiredAtUtc { get; init; }
    public required string RegistryRepoId { get; init; }
    public required string RegistrySourceRevision { get; init; }
    public required GgufRole Role { get; init; }
    public string? ProjectorRelativePath { get; init; }
    public string? ProjectorSourceDisplayName { get; init; }
    public string? ProjectorSourceSha256 { get; init; }
    public long? ProjectorSourceSizeBytes { get; init; }
    public string? ProjectorContentSha256 { get; init; }
    public long? ProjectorContentSizeBytes { get; init; }
    public string? ProjectorMemberFingerprint { get; init; }
    public required string ModelContentFingerprint { get; init; }
}
