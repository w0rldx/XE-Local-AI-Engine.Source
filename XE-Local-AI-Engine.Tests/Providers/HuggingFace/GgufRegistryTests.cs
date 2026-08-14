namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     GGUF registry: list/resolve a present model by name, and self-heal a corrupt/missing manifest by
///     rescanning the models directory. No network.
/// </summary>
public sealed class GgufRegistryTests
{
    private static readonly JsonSerializerOptions RawManifestOptions = new()
    {
        WriteIndented = true
    };

    [Test]
    public async Task GgufRegistry_ListsPresentModels_AndResolvesPathByModelName()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        var entry = new GgufModelRegistryEntry
        {
            ModelName = Infra.ModelName,
            RepoId = Infra.RepoId,
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            Sha256 = null,
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UtcNow,
            Role = GgufRole.Chat
        };
        await registry.UpsertAsync(entry, CancellationToken.None);

        var listed = await registry.ListAsync(CancellationToken.None);
        AssertEx.ContainsSingle(listed, item => item.ModelName == Infra.ModelName);

        var found = await registry.FindAsync(Infra.ModelName, CancellationToken.None);
        AssertEx.NotNull(found);
        AssertEx.Equal(filePath, found!.LocalPath);

        // The LocalModelDescriptor mapping holds: a present entry resolves to a real on-disk path.
        AssertEx.True(File.Exists(found.LocalPath));
    }

    [Test]
    public async Task GgufRegistry_SelfHeals_OnCorruptManifest_ByRescan()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);

        // A .gguf file is on disk but the manifest is corrupt — a rescan must recover it without throwing.
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "index.json"), "{ this is not valid json");

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, item => item.FileName == Infra.FileName && item.Quant == Infra.Quant);
    }

    [Test]
    public async Task GgufRegistry_MissingManifest_RescansDirectory_NoThrow()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);

        await File.WriteAllTextAsync(dir.FilePath("Other-Model-Q5_K_M.gguf"), "fake-gguf");
        // A non-gguf file and a quant-less gguf are ignored by the rescan.
        await File.WriteAllTextAsync(dir.FilePath("readme.txt"), "not a model");
        await File.WriteAllTextAsync(dir.FilePath("no-quant.gguf"), "no recognizable quant");

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, item => item.Quant == "Q5_K_M");
    }

    [Test]
    public async Task GgufRegistry_DeterministicUdFilenameWithoutSidecar_FailsClosed()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        await File.WriteAllTextAsync(dir.FilePath("demo-UD-Q4_K_XL-0123456789abcdef01234567.gguf"), "untrusted");

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.Empty(listed);
    }

    [Test]
    public async Task GgufRegistry_Remove_DropsEntry_Idempotent()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        await registry.UpsertAsync(new GgufModelRegistryEntry
        {
            ModelName = Infra.ModelName,
            RepoId = Infra.RepoId,
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        await registry.RemoveAsync(Infra.ModelName, CancellationToken.None);
        AssertEx.Null(await registry.FindAsync(Infra.ModelName, CancellationToken.None));

        // Idempotent — removing again is a no-op.
        await registry.RemoveAsync(Infra.ModelName, CancellationToken.None);
    }

    [Test]
    public async Task GgufRegistry_DropsEntry_WhenBackingFileDeleted()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        await registry.UpsertAsync(new GgufModelRegistryEntry
        {
            ModelName = Infra.ModelName,
            RepoId = Infra.RepoId,
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        // Manual deletion of the file off-band — the manifest entry must not be returned for a missing file.
        File.Delete(filePath);

        AssertEx.Null(await registry.FindAsync(Infra.ModelName, CancellationToken.None));
    }

    [Test]
    public async Task GgufRegistry_FirstDownloadIntoEmptyDirectory_YieldsExactlyOneCanonicalEntry()
    {
        // Reproduces the first-download double-registration: the .gguf lands on disk with NO manifest, so the upsert's
        // load self-heals via a rescan that registers a filename-alias entry — the canonical upsert must then collapse
        // the alias rather than append a second entry sharing the one file.
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");

        await registry.UpsertAsync(CanonicalEntry(filePath), CancellationToken.None);

        var listed = await registry.ListAsync(CancellationToken.None);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Equal(Infra.ModelName, listed[0].ModelName);

        // The self-healing filename alias must NOT survive as a second entry.
        AssertEx.Null(await registry.FindAsync(FilenameAliasName, CancellationToken.None));
        AssertEx.NotNull(await registry.FindAsync(Infra.ModelName, CancellationToken.None));
    }

    [Test]
    public async Task GgufRegistry_ExistingDuplicatePathManifest_CollapsesOnLoad_PreferringCanonical()
    {
        // An already-affected user's manifest carries two entries (legacy alias + canonical) for one file. Listing must
        // collapse them to the canonical entry without touching the file (migration for affected installs).
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        await WriteRawManifestAsync(dir.Path, AliasEntry(filePath), CanonicalEntry(filePath));

        using var registry = Infra.Registry(options);
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Equal(Infra.ModelName, listed[0].ModelName);
        AssertEx.True(File.Exists(filePath), "collapsing the view must never delete the backing file");
    }

    [Test]
    public async Task GgufStore_DirectDeleteThroughLegacyAlias_IsRejectedWithoutMutation()
    {
        await AssertDeleteRemovesBothEntriesAsync(deleteThroughAlias: true);
    }

    [Test]
    public async Task GgufStore_DirectDeleteThroughCanonicalName_IsRejectedWithoutMutation()
    {
        await AssertDeleteRemovesBothEntriesAsync(deleteThroughAlias: false);
    }

    // Direct provider deletion cannot participate in the application composite lease/journal. It must fail closed and
    // leave the complete legacy alias set untouched; the application deletion coordinator owns the actual mutation.
    private static async Task AssertDeleteRemovesBothEntriesAsync(bool deleteThroughAlias)
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var filePath = dir.FilePath(Infra.FileName);
        await File.WriteAllTextAsync(filePath, "fake-gguf");
        await WriteRawManifestAsync(dir.Path, AliasEntry(filePath), CanonicalEntry(filePath));

        using var registry = Infra.Registry(options);
#pragma warning disable CA2000 // The in-memory fake handler holds no unmanaged resource; the client lives for the test.
        using var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage()));
#pragma warning restore CA2000
        var downloadClient = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var store = Infra.Store(downloadClient, Infra.DiscoveryWith(), registry, options);

        _ = await AssertEx.ThrowsAsync<NotSupportedException>(() =>
                              store.DeleteModelAsync(deleteThroughAlias ? FilenameAliasName : Infra.ModelName, CancellationToken.None))
                          .ConfigureAwait(false);

        AssertEx.True(File.Exists(filePath), "a bypass attempt must not delete the shared backing file");
        AssertEx.Equal(expected: 1, (await registry.ListAsync(CancellationToken.None)).Count);
        AssertEx.NotNull(await registry.FindAsync(FilenameAliasName, CancellationToken.None));
        AssertEx.NotNull(await registry.FindAsync(Infra.ModelName, CancellationToken.None));
    }

    // The filename-derived identity a manifest-absent rescan assigns to the downloaded file (stem + quant).
    private static string FilenameAliasName => GgufModelName.Format(Path.GetFileNameWithoutExtension(Infra.FileName), Infra.Quant);

    // A canonically-registered download: real repo id (org/name), verified hash + revision, known role.
    private static GgufModelRegistryEntry CanonicalEntry(string filePath)
    {
        return new GgufModelRegistryEntry
        {
            ModelName = Infra.ModelName,
            RepoId = Infra.RepoId,
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            Sha256 = "verified-sha256",
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UtcNow,
            Role = GgufRole.Chat
        };
    }

    // A legacy rescan-derived alias for the same file: filename-stem repo id (no '/'), empty revision, null hash, Unknown role.
    private static GgufModelRegistryEntry AliasEntry(string filePath)
    {
        return new GgufModelRegistryEntry
        {
            ModelName = FilenameAliasName,
            RepoId = Path.GetFileNameWithoutExtension(Infra.FileName),
            FileName = Infra.FileName,
            Quant = Infra.Quant,
            LocalPath = filePath,
            SizeBytes = 9,
            Sha256 = null,
            SourceRevision = string.Empty,
            DownloadedAtUtc = DateTimeOffset.UtcNow,
            Role = GgufRole.Unknown
        };
    }

    // Writes the manifest verbatim (bypassing the collapsing upsert) so a pre-migration duplicate-path state can be seeded.
    private static async Task WriteRawManifestAsync(string modelsDirectory, params GgufModelRegistryEntry[] entries)
    {
        var json = JsonSerializer.Serialize(new
        {
            Models = entries
        }, RawManifestOptions);
        await File.WriteAllTextAsync(Path.Combine(modelsDirectory, "index.json"), json);
    }
}
