namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Maps a live installed-model snapshot into the frozen benchmark projection of it.</summary>
internal static class BenchmarkInstalledModelSnapshotMapper
{
    public static BenchmarkInstalledModelSnapshotV1 ToSnapshot(InstalledModelSnapshot source) =>
        new(source.ModelName,
            source.RegistryRevision,
            source.RegistryAliases.Select(static alias => new BenchmarkRegistryAliasSnapshotV1(alias.ModelName, alias.RegistryRevision)).ToArray(),
            source.RegistryAliasSetHash,
            source.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                      member.Role,
                      member.SizeBytes,
                      member.Sha256,
                      member.OwningAliases.ToArray(),
                      member.Required,
                      member.MetadataSchemaVersion,
                      member.MemberFingerprint))
                  .ToArray(),
            source.PhysicalMemberSetHash,
            source.Origin,
            source.ProviderName!,
            source.ProviderMappingRevision,
            source.RepoId,
            source.SourceRevision,
            Path.GetFileName(source.Members.First(static member => member.Role == InstalledModelPhysicalMemberRole.Weight).RelativePath),
            source.Quantization,
            source.Role switch
            {
                GgufRole.Chat => "chat",
                GgufRole.Embedding => "embedding",
                GgufRole.Draft => "draft",
                _ => "unknown"
            },
            source.ModelContentFingerprint);
}
