namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Physical role of an installed GGUF acquisition member.</summary>
public enum InstalledModelPhysicalMemberRole
{
    Weight = 0,

    /// <summary>Optional multimodal projector.</summary>
    Projector = 1,

    /// <summary>Recovery sidecar; not model content.</summary>
    Sidecar = 2
}

/// <summary>Canonical facts used to compute an aggregate model-content fingerprint.</summary>
public sealed record GgufModelContentMember
{
    public required string RelativePath { get; init; }

    public required InstalledModelPhysicalMemberRole Role { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required IReadOnlyList<string> OwningAliases { get; init; }
}
