namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     The manifest self-heal on the read path: a sidecar-backed model missing from <c>index.json</c> is reconciled
///     into the returned list whether or not the manifest can be rewritten.
/// </summary>
/// <remarks>
///     The Windows brick this covers: the reader used to hold <c>index.json</c> open while the self-heal replaced it,
///     so every read threw <see cref="UnauthorizedAccessException" /> for good.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class GgufRegistrySelfHealWriteTests
{
    private const string ModelName = "Local/Demo:Q4_K_M";
    private const string WeightFileName = "demo-Q4_K_M.gguf";

    [Test]
    public async Task GgufRegistry_SidecarReconcile_PersistsTheRecoveredRow()
    {
        using var dir = new Infra.TempModelsDir();
        await SeedSidecarBackedModelWithoutManifestRowAsync(dir.Path);

        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, item => item.ModelName == ModelName);
        var manifest = await File.ReadAllTextAsync(Path.Combine(dir.Path, "index.json"));
        AssertEx.Contains(manifest, ModelName);
        AssertEx.False(File.Exists(Path.Combine(dir.Path, "index.json.tmp")), "the atomic replace must leave no temp behind.");
    }

    [Test]
    public async Task GgufRegistry_SidecarReconcile_SurvivesAnUnwritableManifest()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("SKIPPED — the read-only-directory stand-in for the Windows manifest lock needs Unix file modes.");
        }

        using var dir = new Infra.TempModelsDir();
        await SeedSidecarBackedModelWithoutManifestRowAsync(dir.Path);

        // Windows fails the self-heal write because the reader held index.json open; a read-only directory is the
        // portable stand-in that makes the temp create or the replace fail the same way here.
        File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                                       | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            AssertWritesAreRefused(dir.Path);

            using var registry = Infra.Registry(Infra.Options(dir.Path));
            var listed = await registry.ListAsync(CancellationToken.None);

            AssertEx.ContainsSingle(listed, item => item.ModelName == ModelName);

            // Proves the read survived a self-heal write that really was refused, rather than one that quietly succeeded.
            var manifest = await File.ReadAllTextAsync(Path.Combine(dir.Path, "index.json"));
            AssertEx.False(manifest.Contains(ModelName, StringComparison.Ordinal), "the manifest must be unchanged when it cannot be rewritten.");
            AssertEx.False(File.Exists(Path.Combine(dir.Path, "index.json.tmp")), "a failed write must leave no temp behind.");
        }
        finally
        {
            File.SetUnixFileMode(dir.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Root ignores the directory's write bit, so the lock this test needs would not exist and a green result would
    // prove nothing.
    private static void AssertWritesAreRefused(string directory)
    {
        var probe = Path.Combine(directory, "write-probe.tmp");
        try
        {
            File.WriteAllText(probe, "probe");
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        File.Delete(probe);
        throw new SkipTestException("SKIPPED — this process writes into a read-only directory (running as root), so the unwritable-manifest case cannot be staged.");
    }

    // A weight plus a content-valid acquisition sidecar, and a manifest that exists with no row for it: the sidecar
    // sweep recovers the entry and flips `changed`, which is what triggers the self-heal write.
    private static async Task SeedSidecarBackedModelWithoutManifestRowAsync(string modelsDirectory)
    {
        var weightPath = Path.Combine(modelsDirectory, WeightFileName);
        await File.WriteAllBytesAsync(weightPath, [1, 2, 3, 4]);

        var hash = await GgufAcquisitionSidecar.ComputeSha256Async(weightPath, CancellationToken.None);
        var modelFingerprint = GgufModelContentFingerprint.ComputeV1([
            new GgufModelContentMember
            {
                RelativePath = WeightFileName,
                Role = InstalledModelPhysicalMemberRole.Weight,
                SizeBytes = 4,
                Sha256 = hash,
                OwningAliases = [ModelName]
            }
        ]);

        var entry = new GgufModelRegistryEntry
        {
            ModelName = ModelName,
            RepoId = ModelName,
            FileName = WeightFileName,
            Quant = "Q4_K_M",
            LocalPath = weightPath,
            SizeBytes = 4,
            Sha256 = hash,
            SourceRevision = $"sha256:{hash}",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            Origin = LocalModelOrigin.Imported,
            SourceDisplayName = "source.gguf",
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = modelFingerprint
        };

        var sidecar = new GgufAcquisitionMetadata
        {
            SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            RegistryRevision = GgufRegistryRevision.ComputeV1(entry, modelsDirectory),
            ModelName = entry.ModelName,
            Origin = LocalModelOrigin.Imported,
            LocalFileName = entry.FileName,
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
        };
        await GgufAcquisitionSidecar.WriteAsync(weightPath + GgufAcquisitionSidecar.Suffix, sidecar, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(modelsDirectory, "index.json"), """{ "Models": [] }""");
    }
}
