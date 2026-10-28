namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public enum InstalledModelMutationKind
{
    Acquire,
    Delete,
    Replace
}

public sealed class IntendedInstalledModelMember
{
    public required string RelativePath { get; init; }

    public required InstalledModelPhysicalMemberRole Role { get; init; }
}

public sealed class InstalledModelMutationRequest
{
    public required string ModelName { get; init; }

    public required InstalledModelMutationKind Kind { get; init; }

    public IReadOnlyList<IntendedInstalledModelMember>? IntendedMembers { get; init; }

    public IReadOnlyList<string>? IntendedModelNames { get; init; }
}

public sealed record InstalledModelSnapshot(
    string ModelName,
    string RegistryRevision,
    IReadOnlyList<InstalledModelRegistryAliasSnapshot> RegistryAliases,
    string RegistryAliasSetHash,
    IReadOnlyList<InstalledModelPhysicalMember> Members,
    string PhysicalMemberSetHash,
    LocalModelOrigin? Origin,
    string? ProviderName,
    string? ProviderMappingRevision,
    string RepoId,
    string SourceRevision,
    string Quantization,
    GgufRole Role,
    string ModelContentFingerprint);

/// <summary>
///     The registry-RECORDED view of an installed model: what a catalog listing needs to judge a model without verifying a byte of it.
/// </summary>
/// <remarks>
///     Reading these facts costs one registry read plus one provider-map read, whereas
///     <see cref="IInstalledModelSnapshotCoordinator.AcquireReadSnapshotAsync" /> re-hashes every member file — minutes per call on a real
///     models directory.
/// </remarks>
public sealed class InstalledModelFacts
{
    public required string ModelName { get; init; }

    public required string ProviderName { get; init; }

    public required GgufRole Role { get; init; }

    public required LocalModelOrigin? Origin { get; init; }

    /// <summary>
    ///     The aggregate content identity the registry recorded at acquisition, or <see langword="null" /> for a legacy
    ///     entry that predates the field. A caller that needs the identity itself must fall back to the verified snapshot
    ///     for that one model.
    /// </summary>
    public required string? ModelContentFingerprint { get; init; }
}
