namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal static class BenchmarkSnapshotModelComparer
{
    public static bool Matches(BenchmarkInstalledModelSnapshotV1 expected, InstalledModelSnapshot actual)
    {
        return string.Equals(expected.ModelName, actual.ModelName, StringComparison.Ordinal)
               && string.Equals(expected.RegistryRevision, actual.RegistryRevision, StringComparison.Ordinal)
               && string.Equals(expected.RegistryAliasSetHash, actual.RegistryAliasSetHash, StringComparison.Ordinal)
               && string.Equals(expected.PhysicalMemberSetHash, actual.PhysicalMemberSetHash, StringComparison.Ordinal)
               && expected.Origin == actual.Origin
               && string.Equals(expected.ProviderName, actual.ProviderName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expected.ProviderMappingRevision, actual.ProviderMappingRevision, StringComparison.Ordinal)
               && string.Equals(expected.ModelContentFingerprint, actual.ModelContentFingerprint, StringComparison.Ordinal)
               && Aliases(expected.RegistryAliases).SequenceEqual(Aliases(actual.RegistryAliases), StringComparer.Ordinal)
               && Members(expected.Members).SequenceEqual(Members(actual.Members), StringComparer.Ordinal);
    }

    private static IEnumerable<string> Aliases(IEnumerable<BenchmarkRegistryAliasSnapshotV1> aliases) =>
        aliases.Select(static alias => $"{alias.ModelName}\u001f{alias.RegistryRevision}").Order(StringComparer.Ordinal);

    private static IEnumerable<string> Aliases(IEnumerable<InstalledModelRegistryAliasSnapshot> aliases) =>
        aliases.Select(static alias => $"{alias.ModelName}\u001f{alias.RegistryRevision}").Order(StringComparer.Ordinal);

    private static IEnumerable<string> Members(IEnumerable<BenchmarkPhysicalMemberSnapshotV1> members) =>
        members.Select(static member => Member(member.RelativePath,
                   member.Role,
                   member.SizeBytes,
                   member.Sha256,
                   member.OwningAliases,
                   member.Required,
                   member.MetadataSchemaVersion,
                   member.MemberFingerprint))
               .Order(StringComparer.Ordinal);

    private static IEnumerable<string> Members(IEnumerable<InstalledModelPhysicalMember> members) =>
        members.Select(static member => Member(member.RelativePath,
                   member.Role,
                   member.SizeBytes,
                   member.Sha256,
                   member.OwningAliases,
                   member.Required,
                   member.MetadataSchemaVersion,
                   member.MemberFingerprint))
               .Order(StringComparer.Ordinal);

    private static string Member(string path,
        InstalledModelPhysicalMemberRole role,
        long size,
        string sha256,
        IEnumerable<string> owners,
        bool required,
        int? schema,
        string? fingerprint) =>
        string.Join('\u001f', path, role, size, sha256, string.Join('\u001e', owners.Order(StringComparer.Ordinal)), required, schema, fingerprint);
}
