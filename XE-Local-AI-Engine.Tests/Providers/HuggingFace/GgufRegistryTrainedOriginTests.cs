namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     The <c>trained</c> origin parses everywhere a persisted origin is read, and an origin this build does not know —
///     one a NEWER build wrote — costs only the row that carries it, never the whole manifest.
/// </summary>
public sealed class GgufRegistryTrainedOriginTests
{
    private const string BaseFileName = "Base-Model-Q4_K_M.gguf";
    private const string BaseModelName = "acme/Base-Model-GGUF:Q4_K_M";

    [Test]
    public async Task Registry_UnknownOrigin_RescanTolerates()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var goodPath = dir.FilePath(BaseFileName);
        var futurePath = dir.FilePath("Future-Model-Q5_K_M.gguf");
        await File.WriteAllTextAsync(goodPath, "fake-gguf");
        await File.WriteAllTextAsync(futurePath, "fake-gguf");

        // "holographic" is an origin no build in this tree understands; the strict origin converter throws on it. The
        // row it belongs to must be the ONLY casualty.
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "index.json"),
            $$"""
              {
                "Models": [
                  {{RawEntry(BaseModelName, BaseFileName, goodPath, "Q4_K_M", "huggingface")}},
                  {{RawEntry("acme/Future:Q5_K_M", "Future-Model-Q5_K_M.gguf", futurePath, "Q5_K_M", "holographic")}}
                ]
              }
              """);

        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, entry => entry.ModelName == BaseModelName);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.True(File.Exists(futurePath), "A row this build cannot read must not cost the file it points at.");
    }

    [Test]
    public async Task Registry_TrainedOrigin_LoadsNormally()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var path = dir.FilePath("Tuned-Model-Q4_K_M.gguf");
        await File.WriteAllTextAsync(path, "fake-gguf");
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "index.json"),
            $$"""
              {
                "Models": [
                  {{RawEntry("acme/Tuned:Q4_K_M", "Tuned-Model-Q4_K_M.gguf", path, "Q4_K_M", "trained")}}
                ]
              }
              """);

        using var registry = Infra.Registry(Infra.Options(dir.Path));
        var listed = await registry.ListAsync(CancellationToken.None);

        AssertEx.ContainsSingle(listed, entry => entry.Origin == LocalModelOrigin.Trained);
    }

    [Test]
    public async Task RegistryRevision_WithoutLineage_IsUnchangedByTheNewFields()
    {
        // The lineage block is written into the revision only when an entry carries lineage, so every already-installed
        // model keeps the exact revision recorded before adapters existed. A regression here silently skips them all.
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var entry = new GgufModelRegistryEntry
        {
            ModelName = BaseModelName,
            RepoId = "acme/Base-Model-GGUF",
            FileName = BaseFileName,
            Quant = "Q4_K_M",
            LocalPath = dir.FilePath(BaseFileName),
            SizeBytes = 9,
            Sha256 = null,
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat
        };

        AssertEx.False(GgufRegistryRevision.HasLineage(entry));
        AssertEx.True(GgufRegistryRevision.IsCanonical(GgufRegistryRevision.ComputeV1(entry, dir.Path)));

        var adapter = entry with
        {
            AdapterFileName = "tuned.gguf",
            BaseModelName = "acme/Other:Q4_K_M"
        };
        AssertEx.True(GgufRegistryRevision.HasLineage(adapter));
        AssertEx.NotEqual(GgufRegistryRevision.ComputeV1(entry, dir.Path), GgufRegistryRevision.ComputeV1(adapter, dir.Path));
    }

    [Test]
    public async Task Store_ResolveAdapterLaunch_PairsTheAdapterWithItsBase()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);
        var basePath = dir.FilePath(BaseFileName);
        var adapterPath = dir.FilePath("Tuned-Adapter-Q4_K_M.gguf");
        await File.WriteAllTextAsync(basePath, "fake-gguf");
        await File.WriteAllTextAsync(adapterPath, "fake-adapter");
        await registry.UpsertAsync(Entry(BaseModelName, BaseFileName, basePath), CancellationToken.None);
        await registry.UpsertAsync(Entry("acme/Tuned:Q4_K_M", "Tuned-Adapter-Q4_K_M.gguf", adapterPath) with
        {
            AdapterFileName = "Tuned-Adapter-Q4_K_M.gguf",
            AdapterSizeBytes = 12,
            BaseModelName = BaseModelName
        }, CancellationToken.None);

        var store = NewStore(registry, options);

        // An ordinary model resolves no pair at all.
        AssertEx.Null(await store.ResolveAdapterLaunchAsync(BaseModelName, CancellationToken.None));

        var launch = AssertEx.NotNull(await store.ResolveAdapterLaunchAsync("acme/Tuned:Q4_K_M", CancellationToken.None));
        AssertEx.Equal(basePath, launch.BaseModelFilePath);
        AssertEx.Equal(adapterPath, launch.AdapterFilePath);
        AssertEx.Equal(expected: 12L, launch.AdapterSizeBytes);
    }

    [Test]
    public async Task Store_ResolveAdapterLaunch_WhenBaseIsMissing_FailsWithAClearError()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);
        var adapterPath = dir.FilePath("Tuned-Adapter-Q4_K_M.gguf");
        await File.WriteAllTextAsync(adapterPath, "fake-adapter");
        await registry.UpsertAsync(Entry("acme/Tuned:Q4_K_M", "Tuned-Adapter-Q4_K_M.gguf", adapterPath) with
        {
            AdapterFileName = "Tuned-Adapter-Q4_K_M.gguf",
            AdapterSizeBytes = 12,
            BaseModelName = "acme/NotInstalled:Q4_K_M"
        }, CancellationToken.None);

        var store = NewStore(registry, options);

        var exception = await AssertEx.ThrowsAsync<GgufAdapterBaseModelMissingException>(() =>
            store.ResolveAdapterLaunchAsync("acme/Tuned:Q4_K_M", CancellationToken.None));

        AssertEx.True(exception.Message.Contains("base model", StringComparison.OrdinalIgnoreCase),
            "The failure must name what is missing.");
    }

    private static IGgufModelStore NewStore(GgufModelRegistry registry, HuggingFaceOptions options)
    {
#pragma warning disable CA2000 // The in-memory fake handler holds no unmanaged resource; the client lives for the test.
        var http = new HttpClient(new GgufStoreTestInfrastructure.ScriptedHandler(static (_, _) => new HttpResponseMessage()));
#pragma warning restore CA2000
        return Infra.Store(Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options),
            Infra.DiscoveryWith(),
            registry,
            options);
    }

    private static GgufModelRegistryEntry Entry(string modelName, string fileName, string localPath) =>
        new()
        {
            ModelName = modelName,
            RepoId = "acme/repo",
            FileName = fileName,
            Quant = "Q4_K_M",
            LocalPath = localPath,
            SizeBytes = 9,
            Sha256 = null,
            SourceRevision = "abc123",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat
        };

    private static string RawEntry(string modelName, string fileName, string localPath, string quant, string origin) =>
        $$"""
          {
              "ModelName": "{{modelName}}",
              "RepoId": "acme/repo",
              "FileName": "{{fileName}}",
              "Quant": "{{quant}}",
              "LocalPath": {{JsonSerializer.Serialize(localPath)}},
              "SizeBytes": 9,
              "SourceRevision": "abc123",
              "DownloadedAtUtc": "1970-01-01T00:00:00+00:00",
              "Role": 1,
              "Origin": "{{origin}}"
          }
          """;
}
