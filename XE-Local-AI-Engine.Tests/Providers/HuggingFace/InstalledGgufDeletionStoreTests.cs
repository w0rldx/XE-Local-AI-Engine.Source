namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InstalledGgufDeletionStoreTests
{
    [Test]
    public async Task StageRemoveRestore_RoundTripsExactMemberAndRegistryValue()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedLegacyAsync(directory.Path, registry).ConfigureAwait(false);
        var snapshotStore = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await snapshotStore.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None)
                                                           .ConfigureAwait(false));
        var snapshot = await snapshotStore.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);
        var store = new InstalledGgufDeletionStore(registry, options);

        var staged = await store.StageAsync(snapshot, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        AssertEx.False(File.Exists(entry.LocalPath));
        AssertEx.True(File.Exists(Path.Combine(directory.Path, staged.StagedMembers.Single().QuarantineRelativePath)));

        var registryReceipt = await store.RemoveAliasesByLocalPathAsync(staged, staged.RemovalAliases, CancellationToken.None)
                                         .ConfigureAwait(false);
        await store.RestoreAsync(staged, registryReceipt, CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(File.Exists(entry.LocalPath));
        var restored = AssertEx.NotNull(await registry.FindAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(AssertEx.NotNull(entry.RegistryRevision), restored.RegistryRevision);
    }

    [Test]
    public async Task Purge_RemovesOnlyOperationOwnedQuarantine()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedLegacyAsync(directory.Path, registry).ConfigureAwait(false);
        var snapshotStore = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await snapshotStore.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None)
                                                           .ConfigureAwait(false));
        var snapshot = await snapshotStore.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);
        var store = new InstalledGgufDeletionStore(registry, options);
        var unrelated = Path.Combine(directory.Path, "unrelated-Q4_K_M.gguf");
        await File.WriteAllBytesAsync(unrelated, [9, 9, 9]).ConfigureAwait(false);

        var staged = await store.StageAsync(snapshot, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        _ = await store.RemoveAliasesByLocalPathAsync(staged, staged.RemovalAliases, CancellationToken.None).ConfigureAwait(false);
        await store.PurgeAsync(staged, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(entry.LocalPath));
        AssertEx.True(File.Exists(unrelated));
        AssertEx.Null(await registry.FindAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));
    }

    [Test]
    public async Task Restore_WhenOriginalWasRacedIn_FailsWithoutOverwritingEitherFile()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedLegacyAsync(directory.Path, registry).ConfigureAwait(false);
        var snapshotStore = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await snapshotStore.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None)
                                                           .ConfigureAwait(false));
        var snapshot = await snapshotStore.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);
        var store = new InstalledGgufDeletionStore(registry, options);
        var staged = await store.StageAsync(snapshot, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllBytesAsync(entry.LocalPath, [8, 8, 8]).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<IOException>(() => store.RestoreAsync(staged, registryAliasReceipt: null, CancellationToken.None))
                          .ConfigureAwait(false);

        var racedBytes = await File.ReadAllBytesAsync(entry.LocalPath).ConfigureAwait(false);
        AssertEx.True(new byte[] { 8, 8, 8 }.AsSpan().SequenceEqual(racedBytes));
        AssertEx.True(File.Exists(Path.Combine(directory.Path, staged.StagedMembers.Single().QuarantineRelativePath)));
    }

    [Test]
    public void CreateStageReceipt_RetainsMemberOwnedBySurvivingAlias()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        var aliases = new[]
        {
            Alias("remove", "remove-Q4_K_M.gguf", hash),
            Alias("survive", "survive-Q4_K_M.gguf", hash, "shared-mmproj.gguf")
        };
        var members = new[]
        {
            Member("remove-Q4_K_M.gguf", InstalledModelPhysicalMemberRole.Weight, hash, ["remove"]),
            Member("survive-Q4_K_M.gguf", InstalledModelPhysicalMemberRole.Weight, hash, ["survive"]),
            Member("shared-mmproj.gguf", InstalledModelPhysicalMemberRole.Projector, hash, ["remove", "survive"])
        };
        var snapshot = new InstalledGgufSnapshot("remove",
            aliases[0].RegistryRevision,
            aliases,
            GgufRegistryAliasSetHash.ComputeV1(aliases),
            members,
            GgufPhysicalMemberSetHash.ComputeV1(members),
            Origin: null,
            RepoId: "remove",
            SourceRevision: string.Empty,
            Quantization: "Q4_K_M",
            GgufRole.Chat,
            GgufModelContentFingerprint.ComputeV1(members.Select(static member =>
                new GgufModelContentMember(member.RelativePath, member.Role, member.SizeBytes, member.Sha256, member.OwningAliases))));

        var receipt = GgufDeletionStageReceipt.Create(snapshot, Guid.NewGuid());

        AssertEx.ContainsSingle(receipt.StagedMembers, member => member.OriginalRelativePath == "remove-Q4_K_M.gguf");
        AssertEx.Contains(receipt.RetainedMembers, member => member.RelativePath == "shared-mmproj.gguf");
    }

    private static async Task<GgufModelRegistryEntry> SeedLegacyAsync(string root, GgufModelRegistry registry)
    {
        var path = Path.Combine(root, "demo-Q4_K_M.gguf");
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var entry = new GgufModelRegistryEntry
        {
            ModelName = "local/demo:Q4_K_M",
            RepoId = "local/demo",
            FileName = Path.GetFileName(path),
            Quant = "Q4_K_M",
            LocalPath = path,
            SizeBytes = bytes.Length,
            Sha256 = hash,
            SourceRevision = "revision",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat
        };
        await registry.UpsertAsync(entry, CancellationToken.None).ConfigureAwait(false);
        return AssertEx.NotNull(await registry.FindAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));
    }

    private static InstalledModelRegistryAliasSnapshot Alias(string modelName,
        string weight,
        string hash,
        string? projector = null)
    {
        var value = new InstalledGgufRegistryValue(modelName,
            weight,
            "Q4_K_M",
            weight,
            3,
            hash,
            string.Empty,
            DateTimeOffset.UnixEpoch,
            GgufRole.Chat,
            projector,
            projector,
            projector is null ? null : 3,
            projector is null ? null : hash,
            Origin: null,
            SourceDisplayName: null,
            MetadataSchemaVersion: null,
            ModelContentFingerprint: null);
        return new InstalledModelRegistryAliasSnapshot(modelName, value, $"v1:{new string('a', 64)}", weight, projector, SidecarRelativePath: null);
    }

    private static InstalledModelPhysicalMember Member(string path,
        InstalledModelPhysicalMemberRole role,
        string hash,
        IReadOnlyList<string> owners) =>
        new(path, role, 3, hash, GgufMemberFingerprint.Compute(hash, 3), owners, Required: true, MetadataSchemaVersion: null);
}
