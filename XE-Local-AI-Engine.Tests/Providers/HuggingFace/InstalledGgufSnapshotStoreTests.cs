namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the registry-fingerprint verification against the hex-case skew between eras: entries written before the
///     sidecar era persisted UPPERCASE SHA-256 hex, while every current writer (and the freshly computed member hash)
///     is lowercase. An ordinal compare rejected every such entry, which failed whole catalog endpoints that verify
///     each installed model.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InstalledGgufSnapshotStoreTests
{
    [Test]
    public async Task LoadVerified_AcceptsLegacyUppercaseRegistrySha()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAsync(directory.Path, registry, Convert.ToHexString);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));

        var snapshot = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);

        AssertEx.Equal(entry.ModelName, snapshot.ModelName);
        // The physical member always carries the canonical lowercase digest, whatever case the registry recorded.
        AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData(WeightBytes)), snapshot.Members.Single().Sha256);
    }

    [Test]
    public async Task LoadVerified_StillRejectsADifferentRegistrySha()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAsync(directory.Path, registry, static _ => new string('a', 64));
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));

        var exception = await AssertEx.ThrowsAsync<InstalledGgufSnapshotException>(() => store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None));

        AssertEx.Equal("InstalledModelMemberFingerprintMismatch", exception.Code);
    }

    [Test]
    public async Task LoadVerified_ReHashesOnlyWhenTheMemberFileChanged()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAsync(directory.Path, registry, Convert.ToHexStringLower);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));

        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);
        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);
        var (hitsWhenUnchanged, missesWhenUnchanged) = (store.MemberHashMemo.Hits, store.MemberHashMemo.Misses);

        // A re-write the memo MUST notice: same bytes, so the digest is unchanged and verification still passes, but
        // the timestamp moved, which is exactly the key half that has to invalidate.
        var weightPath = AssertEx.NotNull(entry.LocalPath);
        File.SetLastWriteTimeUtc(weightPath, File.GetLastWriteTimeUtc(weightPath).AddMinutes(5));
        var snapshot = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);

        AssertEx.Equal(expected: 1L, hitsWhenUnchanged, "the second acquire of an unchanged member must not re-hash it");
        AssertEx.Equal(expected: 1L, missesWhenUnchanged, "only the first acquire may hash the member");
        AssertEx.Equal(expected: 2L, store.MemberHashMemo.Misses, "a moved last-write time must re-hash");
        AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData(WeightBytes)), snapshot.Members.Single().Sha256);
    }

    [Test]
    public async Task LoadVerified_AcquiredEntry_ValidatesTheSidecarAgainstTheMemoisedDigest()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAcquiredAsync(directory.Path, registry);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));

        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);
        var (hitsAfterFirst, missesAfterFirst) = (store.MemberHashMemo.Hits, store.MemberHashMemo.Misses);
        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);

        // First acquire: weight and sidecar file are hashed once each; the sidecar check then takes the weight digest
        // from the memo. Repeat acquire: all three lookups hit, so nothing is read in full again.
        AssertEx.Equal(expected: 2L, missesAfterFirst, "the first acquire hashes each member exactly once");
        AssertEx.Equal(expected: 1L, hitsAfterFirst, "the sidecar check must reuse the weight digest just computed");
        AssertEx.Equal(expected: 2L, store.MemberHashMemo.Misses, "a repeat acquire of an unchanged acquired model must not hash");
        AssertEx.Equal(expected: 4L, store.MemberHashMemo.Hits, "the repeat acquire's sidecar check must come from the memo");
    }

    [Test]
    public async Task LoadVerified_AcquiredEntry_StillRejectsAMismatchedSidecarOnAMemoHit()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAcquiredAsync(directory.Path, registry);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));
        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);

        // Self-consistent (its revision recomputes, so the registry does not repair it) but recording a digest the weight
        // lacks. The weight is untouched, so its digest is a memo hit and only the comparison can catch the mismatch.
        var sidecarPath = entry.LocalPath + GgufAcquisitionSidecar.Suffix;
        var metadata = AssertEx.NotNull(await GgufAcquisitionSidecar.ReadShapeValidAsync(sidecarPath, entry.LocalPath, directory.Path,
            CancellationToken.None));
        var otherSha = new string('b', 64);
        var tampered = metadata with
        {
            WeightContentSha256 = otherSha,
            WeightMemberFingerprint = GgufMemberFingerprint.Compute(otherSha, metadata.WeightSizeBytes),
            RegistrySourceRevision = $"sha256:{otherSha}"
        };
        tampered = tampered with
        {
            RegistryRevision = AssertEx.NotNull(GgufAcquisitionSidecar.ToRegistryEntry(tampered, entry.LocalPath, directory.Path).RegistryRevision)
        };
        File.Delete(sidecarPath);
        await GgufAcquisitionSidecar.WriteAsync(sidecarPath, tampered, CancellationToken.None);
        // Same length as the original, so move the timestamp too: the rewritten sidecar member must be a memo miss.
        File.SetLastWriteTimeUtc(sidecarPath, File.GetLastWriteTimeUtc(sidecarPath).AddMinutes(5));
        var hitsBefore = store.MemberHashMemo.Hits;

        var exception = await AssertEx.ThrowsAsync<InstalledGgufSnapshotException>(() => store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None));

        AssertEx.Equal("InstalledModelSidecarInvalid", exception.Code);
        AssertEx.Equal(hitsBefore + 2, store.MemberHashMemo.Hits, "the weight digest (member + sidecar check) came from the memo");
    }

    [Test]
    public async Task LoadVerified_AcquiredEntry_ReHashesOnceWhenTheWeightTimestampMoves()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAcquiredAsync(directory.Path, registry);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None));
        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);
        var missesBefore = store.MemberHashMemo.Misses;

        File.SetLastWriteTimeUtc(entry.LocalPath, File.GetLastWriteTimeUtc(entry.LocalPath).AddMinutes(5));
        var snapshot = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None);

        AssertEx.Equal(missesBefore + 1, store.MemberHashMemo.Misses, "a moved weight timestamp re-hashes the weight exactly once");
        AssertEx.Equal(AssertEx.NotNull(entry.ModelContentFingerprint), snapshot.ModelContentFingerprint);
    }

    [Test]
    public void HashMemo_MissesOnEveryKeyChangeAndStaysBounded()
    {
        var memo = new GgufMemberHashMemo();
        var stamp = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
        memo.Set("/models/weight.gguf", length: 4, stamp, "digest");

        AssertEx.Equal("digest", memo.TryGet("/models/weight.gguf", length: 4, stamp));
        AssertEx.Null(memo.TryGet("/models/weight.gguf", length: 5, stamp), "a changed length must re-hash");
        AssertEx.Null(memo.TryGet("/models/weight.gguf", length: 4, stamp.AddTicks(1)), "a changed timestamp must re-hash");
        AssertEx.Null(memo.TryGet("/models/other.gguf", length: 4, stamp), "another file is another entry");

        for (var index = 0; index <= GgufMemberHashMemo.MaxEntries; index++)
        {
            memo.Set($"/models/{index}.gguf", length: 4, stamp, "digest");
        }

        AssertEx.Null(memo.TryGet("/models/weight.gguf", length: 4, stamp), "the bound must drop remembered entries, never grow past it");
    }

    private static byte[] WeightBytes =>
    [
        1,
        2,
        3,
        4
    ];

    // An imported (Origin set) weight with its content-valid acquisition sidecar: the shape the acquire path checks
    // the sidecar for.
    private static async Task<GgufModelRegistryEntry> SeedAcquiredAsync(string root, GgufModelRegistry registry)
    {
        const string modelName = "local/imported:Q4_K_M";
        const string fileName = "imported-Q4_K_M.gguf";
        var path = Path.Combine(root, fileName);
        var bytes = WeightBytes;
        await File.WriteAllBytesAsync(path, bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var modelFingerprint = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember
            {
                RelativePath = fileName,
                Role = InstalledModelPhysicalMemberRole.Weight,
                SizeBytes = bytes.Length,
                Sha256 = hash,
                OwningAliases = [modelName]
            }
        ]);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = modelName,
            RepoId = modelName,
            FileName = fileName,
            Quant = "Q4_K_M",
            LocalPath = path,
            SizeBytes = bytes.Length,
            Sha256 = hash,
            SourceRevision = $"sha256:{hash}",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            Origin = LocalModelOrigin.Imported,
            SourceDisplayName = "source.gguf",
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = modelFingerprint
        };
        await GgufAcquisitionSidecar.WriteAsync(path + GgufAcquisitionSidecar.Suffix, new GgufAcquisitionMetadata
        {
            SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            RegistryRevision = GgufRegistryRevision.ComputeV1(entry, root),
            ModelName = modelName,
            Origin = LocalModelOrigin.Imported,
            LocalFileName = fileName,
            Quantization = entry.Quant,
            WeightContentSha256 = hash,
            WeightSizeBytes = entry.SizeBytes,
            WeightMemberFingerprint = GgufMemberFingerprint.Compute(hash, entry.SizeBytes),
            SourceDisplayName = "source.gguf",
            AcquiredAtUtc = entry.DownloadedAtUtc,
            RegistryRepoId = entry.RepoId,
            RegistrySourceRevision = entry.SourceRevision,
            Role = entry.Role,
            ModelContentFingerprint = modelFingerprint
        }, CancellationToken.None);
        await registry.UpsertAsync(entry, CancellationToken.None);
        return AssertEx.NotNull(await registry.FindAsync(modelName, CancellationToken.None));
    }

    private static async Task<GgufModelRegistryEntry> SeedAsync(string root, GgufModelRegistry registry, Func<byte[], string> formatSha)
    {
        var path = Path.Combine(root, "legacy-Q4_K_M.gguf");
        var bytes = WeightBytes;
        await File.WriteAllBytesAsync(path, bytes);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = "local/legacy:Q4_K_M",
            RepoId = "local/legacy",
            FileName = Path.GetFileName(path),
            Quant = "Q4_K_M",
            LocalPath = path,
            SizeBytes = bytes.Length,
            Sha256 = formatSha(SHA256.HashData(bytes)),
            SourceRevision = "revision",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat
        };
        await registry.UpsertAsync(entry, CancellationToken.None);
        return AssertEx.NotNull(await registry.FindAsync(entry.ModelName, CancellationToken.None));
    }
}
