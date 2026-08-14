namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Globalization;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>Verified immutable facts for one installed physical acquisition member.</summary>
public sealed record InstalledModelPhysicalMember(
    string RelativePath,
    InstalledModelPhysicalMemberRole Role,
    long SizeBytes,
    string Sha256,
    string? MemberFingerprint,
    IReadOnlyList<string> OwningAliases,
    bool Required,
    int? MetadataSchemaVersion);

/// <summary>Immutable registry material with provider-root-relative paths.</summary>
public sealed record InstalledGgufRegistryValue(
    string RepoId,
    string FileName,
    string Quant,
    string WeightRelativePath,
    long SizeBytes,
    string? Sha256,
    string SourceRevision,
    DateTimeOffset DownloadedAtUtc,
    GgufRole Role,
    string? ProjectorFileName,
    string? ProjectorRelativePath,
    long? ProjectorSizeBytes,
    string? ProjectorSha256,
    LocalModelOrigin? Origin,
    string? SourceDisplayName,
    int? MetadataSchemaVersion,
    string? ModelContentFingerprint);

/// <summary>Exact registry alias value and paths captured for a verified snapshot.</summary>
public sealed record InstalledModelRegistryAliasSnapshot(
    string ModelName,
    InstalledGgufRegistryValue RegistryValue,
    string RegistryRevision,
    string WeightRelativePath,
    string? ProjectorRelativePath,
    string? SidecarRelativePath);

/// <summary>Optimistic discovery hint used only to choose coordination keys.</summary>
public sealed record InstalledGgufCandidate(
    string ModelName,
    IReadOnlyList<InstalledModelRegistryAliasSnapshot> RegistryAliases,
    IReadOnlyList<string> MemberRelativePaths);

/// <summary>Provider-verified installed GGUF snapshot without absolute filesystem paths.</summary>
public sealed record InstalledGgufSnapshot(
    string ModelName,
    string RegistryRevision,
    IReadOnlyList<InstalledModelRegistryAliasSnapshot> RegistryAliases,
    string RegistryAliasSetHash,
    IReadOnlyList<InstalledModelPhysicalMember> Members,
    string PhysicalMemberSetHash,
    LocalModelOrigin? Origin,
    string RepoId,
    string SourceRevision,
    string Quantization,
    GgufRole Role,
    string ModelContentFingerprint);

/// <summary>Provider-owned discovery and verification seam used by installed-model coordination.</summary>
public interface IInstalledGgufSnapshotStore
{
    Task<InstalledGgufCandidate?> DiscoverCandidateAsync(string modelName, CancellationToken cancellationToken);

    Task<InstalledGgufSnapshot> LoadVerifiedAsync(string modelName,
        InstalledGgufCandidate expectedCandidate,
        CancellationToken cancellationToken);
}

/// <summary>Sanitized installed-snapshot verification failure.</summary>
public sealed class InstalledGgufSnapshotException : Exception
{
    public InstalledGgufSnapshotException(string code, string sanitizedMessage)
        : base(sanitizedMessage)
    {
        Code = code;
    }

    public InstalledGgufSnapshotException(string code, string sanitizedMessage, Exception innerException)
        : base(sanitizedMessage, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Canonical V1 hash of exact model-name/registry-revision alias pairs.</summary>
public static class GgufRegistryAliasSetHash
{
    public static string ComputeV1(IEnumerable<InstalledModelRegistryAliasSnapshot> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        using var buffer = new MemoryStream();
        GgufModelContentFingerprint.WriteField(buffer, "gguf-registry-alias-set-v1");
        foreach (var alias in aliases.OrderBy(static alias => alias.ModelName, StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(static alias => alias.ModelName, StringComparer.Ordinal))
        {
            GgufModelContentFingerprint.WriteField(buffer, alias.ModelName);
            GgufModelContentFingerprint.WriteField(buffer, alias.RegistryRevision);
        }

        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>Canonical V1 hash of the complete physical-member closure.</summary>
public static class GgufPhysicalMemberSetHash
{
    public static string ComputeV1(IEnumerable<InstalledModelPhysicalMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        using var buffer = new MemoryStream();
        GgufModelContentFingerprint.WriteField(buffer, "gguf-physical-member-set-v1");
        foreach (var member in members.OrderBy(static member => member.RelativePath, StringComparer.Ordinal)
                                      .ThenBy(static member => member.Role))
        {
            if (member.SizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(members), "A physical member size cannot be negative.");
            }

            if (member.Role == InstalledModelPhysicalMemberRole.Sidecar
                ? member.MemberFingerprint is not null || member.MetadataSchemaVersion is null
                : !string.Equals(member.MemberFingerprint, GgufMemberFingerprint.Compute(member.Sha256, member.SizeBytes), StringComparison.Ordinal)
                  || member.MetadataSchemaVersion is not null)
            {
                throw new ArgumentException("The physical member fingerprint/schema combination is invalid.", nameof(members));
            }

            GgufModelContentFingerprint.WriteField(buffer, member.Role.ToString().ToLowerInvariant());
            GgufModelContentFingerprint.WriteField(buffer, GgufModelContentFingerprint.NormalizeRelativePath(member.RelativePath));
            GgufModelContentFingerprint.WriteField(buffer, member.SizeBytes.ToString(CultureInfo.InvariantCulture));
            GgufMemberFingerprint.ValidateHash(member.Sha256);
            GgufModelContentFingerprint.WriteField(buffer, member.Sha256);
            GgufModelContentFingerprint.WriteField(buffer, member.Required ? "true" : "false");
            GgufModelContentFingerprint.WriteField(buffer,
                member.MetadataSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "null");
            var aliases = member.OwningAliases.OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(static alias => alias, StringComparer.Ordinal)
                                .ToArray();
            GgufModelContentFingerprint.WriteField(buffer, aliases.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var alias in aliases)
            {
                GgufModelContentFingerprint.WriteField(buffer, alias);
            }
        }

        return "v1:" + Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
