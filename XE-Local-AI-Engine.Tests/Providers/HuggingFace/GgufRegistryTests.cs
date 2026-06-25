namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     GGUF registry: list/resolve a present model by name, and self-heal a corrupt/missing manifest by
///     rescanning the models directory. No network.
/// </summary>
public sealed class GgufRegistryTests
{
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
}
