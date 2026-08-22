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
