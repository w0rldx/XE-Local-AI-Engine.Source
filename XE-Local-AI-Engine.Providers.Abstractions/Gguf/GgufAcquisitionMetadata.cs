namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Versioned recovery metadata written next to every newly acquired GGUF.</summary>
public sealed record GgufAcquisitionMetadata
{
    /// <summary>Version written by new acquisitions. v2 added the trained-model lineage + adapter member fields.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    ///     Oldest sidecar version still accepted on read. Every field v2 added is optional, so a v1 sidecar written by an
    ///     earlier install IS a valid v2 document with null lineage — rejecting it would strand every already-installed
    ///     model behind a "corrupt sidecar" repair loop on first launch after the upgrade.
    /// </summary>
    public const int MinimumSupportedSchemaVersion = 1;

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

    /// <summary>Base checkpoint repo a training run derived this model from; null for every non-trained acquisition.</summary>
    public string? DerivedFromRepoId { get; init; }

    /// <summary>Resolved revision of <see cref="DerivedFromRepoId" />.</summary>
    public string? DerivedFromRevision { get; init; }

    /// <summary>Frozen training-dataset content fingerprint the run consumed.</summary>
    public string? DerivedFromContentFingerprint { get; init; }

    /// <summary>Adapter file name when this sidecar describes a LoRA adapter rather than a standalone model.</summary>
    public string? AdapterFileName { get; init; }

    /// <summary>Lowercase SHA-256 of the adapter bytes.</summary>
    public string? AdapterSha256 { get; init; }

    /// <summary>Adapter size in bytes.</summary>
    public long? AdapterSizeBytes { get; init; }

    /// <summary>Canonical member fingerprint over the adapter bytes.</summary>
    public string? AdapterMemberFingerprint { get; init; }

    /// <summary>Registry name of the installed base model an adapter launches against.</summary>
    public string? BaseModelName { get; init; }
}
