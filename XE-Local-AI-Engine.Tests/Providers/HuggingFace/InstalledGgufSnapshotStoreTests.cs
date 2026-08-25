namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the registry-fingerprint verification against the hex-case skew between eras: entries written before the
///     sidecar era persisted UPPERCASE SHA-256 hex, while every current writer (and the freshly computed member hash)
///     is lowercase. An ordinal compare rejected every such entry, which failed whole catalog endpoints that verify
///     each installed model.
/// </summary>
public sealed class InstalledGgufSnapshotStoreTests
{
    [Test]
    public async Task LoadVerified_AcceptsLegacyUppercaseRegistrySha()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAsync(directory.Path, registry, Convert.ToHexString).ConfigureAwait(false);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));

        var snapshot = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);

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
        var entry = await SeedAsync(directory.Path, registry, static _ => new string('a', 64)).ConfigureAwait(false);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));

        var exception = await AssertEx.ThrowsAsync<InstalledGgufSnapshotException>(() => store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None))
                                      .ConfigureAwait(false);

        AssertEx.Equal("InstalledModelMemberFingerprintMismatch", exception.Code);
    }

    [Test]
    public async Task LoadVerified_ReHashesOnlyWhenTheMemberFileChanged()
    {
        using var directory = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = GgufStoreTestInfrastructure.Options(directory.Path);
        using var registry = GgufStoreTestInfrastructure.Registry(options);
        var entry = await SeedAsync(directory.Path, registry, Convert.ToHexStringLower).ConfigureAwait(false);
        var store = new InstalledGgufSnapshotStore(registry, options);
        var candidate = AssertEx.NotNull(await store.DiscoverCandidateAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));

        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);
        _ = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);
        var (hitsWhenUnchanged, missesWhenUnchanged) = (store.MemberHashMemo.Hits, store.MemberHashMemo.Misses);

        // A re-write the memo MUST notice: same bytes, so the digest is unchanged and verification still passes, but
        // the timestamp moved, which is exactly the key half that has to invalidate.
        var weightPath = AssertEx.NotNull(entry.LocalPath);
        File.SetLastWriteTimeUtc(weightPath, File.GetLastWriteTimeUtc(weightPath).AddMinutes(5));
        var snapshot = await store.LoadVerifiedAsync(entry.ModelName, candidate, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, hitsWhenUnchanged, "the second acquire of an unchanged member must not re-hash it");
        AssertEx.Equal(expected: 1L, missesWhenUnchanged, "only the first acquire may hash the member");
        AssertEx.Equal(expected: 2L, store.MemberHashMemo.Misses, "a moved last-write time must re-hash");
        AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData(WeightBytes)), snapshot.Members.Single().Sha256);
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

    private static async Task<GgufModelRegistryEntry> SeedAsync(string root, GgufModelRegistry registry, Func<byte[], string> formatSha)
    {
        var path = Path.Combine(root, "legacy-Q4_K_M.gguf");
        var bytes = WeightBytes;
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
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
        await registry.UpsertAsync(entry, CancellationToken.None).ConfigureAwait(false);
        return AssertEx.NotNull(await registry.FindAsync(entry.ModelName, CancellationToken.None).ConfigureAwait(false));
    }
}
