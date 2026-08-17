namespace XE_Local_AI_Engine.Tests.Training.Runs;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Live-found: nothing linked a run to its installed base, so adapters could never be smoke-tested or promoted.
///     The linker resolves the official <c>&lt;base&gt;-GGUF</c> repo (or the same repo id) and refuses to guess otherwise.
/// </summary>
public sealed class InstalledBaseModelLinkerTests
{
    private const string BaseRepo = "Qwen/Qwen2.5-0.5B-Instruct";

    [Test]
    public async Task Suggest_PrefersTheSameRepoThenTheOfficialGgufRepo_AndSkipsAdapters()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        _ = registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Entry("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M", "bartowski/Qwen2.5-0.5B-Instruct-GGUF"),
            Entry("Qwen/Qwen2.5-0.5B-Instruct-GGUF:Q8_0", "Qwen/Qwen2.5-0.5B-Instruct-GGUF"),
            Entry("Qwen/Qwen2.5-0.5B-Instruct:F16", "Qwen/Qwen2.5-0.5B-Instruct"),
            Entry("my-adapter", "Qwen/Qwen2.5-0.5B-Instruct-GGUF", baseModelName: "Qwen/Qwen2.5-0.5B-Instruct-GGUF:Q8_0")
        });
        var linker = new InstalledBaseModelLinker(registry);

        var suggestions = await linker.SuggestAsync(BaseRepo);

        AssertEx.Equal(expected: 2, suggestions.Count);
        AssertEx.Equal("Qwen/Qwen2.5-0.5B-Instruct:F16", suggestions[0].ModelName);
        AssertEx.Equal("Qwen/Qwen2.5-0.5B-Instruct-GGUF:Q8_0", suggestions[1].ModelName);
    }

    [Test]
    public async Task Resolve_ExplicitName_MustBeInstalled()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        _ = registry.FindAsync("missing", Arg.Any<CancellationToken>()).Returns((GgufModelRegistryEntry?)null);
        var linker = new InstalledBaseModelLinker(registry);

        _ = await AssertEx.ThrowsAsync<TrainingRunRejectedException>(() => linker.ResolveAsync(BaseRepo, "missing"));
    }

    [Test]
    public async Task Resolve_NoMatch_IsNull_NeverAGuess()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        _ = registry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            Entry("other/Model-GGUF:Q4", "other/Model-GGUF")
        });
        var linker = new InstalledBaseModelLinker(registry);

        AssertEx.Null(await linker.ResolveAsync(BaseRepo, explicitModelName: null));
    }

    private static GgufModelRegistryEntry Entry(string modelName, string repoId, string? baseModelName = null) =>
        new()
        {
            ModelName = modelName,
            RepoId = repoId,
            FileName = "model.gguf",
            Quant = "Q8_0",
            LocalPath = "/models/model.gguf",
            SizeBytes = 1,
            SourceRevision = "main",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            ModelContentFingerprint = "fp:" + modelName,
            BaseModelName = baseModelName
        };
}
