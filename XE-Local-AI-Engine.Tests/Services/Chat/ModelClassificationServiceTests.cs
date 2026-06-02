namespace XE_Local_AI_Engine.Tests.Services.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ModelClassificationServiceTests
{
    [Test]
    public async Task ClassifyAsync_WhenUnclassified_DetectsAndCachesEffectiveKind()
    {
        var (service, store, ollama) = CreateService();
        StubDetails(ollama, "llama3.1", "completion", "tools");

        var results = await service.ClassifyAsync([("llama3.1", "sha256:a")]).ConfigureAwait(false);

        var result = results["llama3.1"];
        AssertEx.Equal(ModelKind.Chat, result.Kind);
        AssertEx.Equal(ModelKind.Chat, result.DetectedKind);
        AssertEx.False(result.IsOverridden, "A freshly detected model is not overridden.");
        AssertEx.Contains(result.Capabilities, "completion");
        AssertEx.Contains(result.Capabilities, "tools");

        // The detection result was persisted, so a later lookup reflects the cached kind and digest.
        var cached = AssertEx.NotNull(await store.GetByNameAsync("llama3.1").ConfigureAwait(false), "Detection should persist a row.");
        AssertEx.Equal(ModelKind.Chat, cached.DetectedKind);
        AssertEx.Equal("sha256:a", cached.Digest);
        await ollama.Received(1).ShowModelDetailsAsync("llama3.1", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ClassifyAsync_WhenCachedWithMatchingDigest_DoesNotProbeAgain()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("phi3", "sha256:same", ModelKind.Chat, """["completion"]""").ConfigureAwait(false);

        var results = await service.ClassifyAsync([("phi3", "sha256:same")]).ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Chat, results["phi3"].Kind);
        // Record present with a matching digest is a cache hit — no /api/show call is issued.
        await ollama.DidNotReceive().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ClassifyAsync_WhenDigestChanged_ReDetects()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("gemma", "sha256:old", ModelKind.Chat, """["completion"]""").ConfigureAwait(false);
        StubDetails(ollama, "gemma", "embedding");

        var results = await service.ClassifyAsync([("gemma", "sha256:new")]).ConfigureAwait(false);

        // The digest moved, so the cache is stale and a fresh probe reclassifies the model.
        AssertEx.Equal(ModelKind.Embedding, results["gemma"].Kind);
        await ollama.Received(1).ShowModelDetailsAsync("gemma", Arg.Any<CancellationToken>()).ConfigureAwait(false);

        var cached = AssertEx.NotNull(await store.GetByNameAsync("gemma").ConfigureAwait(false), "Re-detection should persist.");
        AssertEx.Equal("sha256:new", cached.Digest);
        AssertEx.Equal(ModelKind.Embedding, cached.DetectedKind);
    }

    [Test]
    public async Task ClassifyAsync_WhenOverrideSet_OverrideWinsOverDetected()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("mistral", "sha256:m", ModelKind.Chat, """["completion"]""").ConfigureAwait(false);
        _ = await store.SetOverrideAsync("mistral", ModelKind.Embedding).ConfigureAwait(false);

        var results = await service.ClassifyAsync([("mistral", "sha256:m")]).ConfigureAwait(false);

        var result = results["mistral"];
        AssertEx.Equal(ModelKind.Embedding, result.Kind, "The override must win over the detected kind.");
        AssertEx.Equal(ModelKind.Chat, result.DetectedKind, "The detected kind is still surfaced for a reset affordance.");
        AssertEx.True(result.IsOverridden, "An overridden model reports IsOverridden.");
        await ollama.DidNotReceive().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ClassifyAsync_WhenDetectionThrows_FallsBackToCachedRecordWithoutThrowing()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("llava", "sha256:old", ModelKind.Chat, """["completion","vision"]""").ConfigureAwait(false);
        ollama.ShowModelDetailsAsync("llava", Arg.Any<CancellationToken>())
              .Returns<OllamaModelDetails>(_ => throw new HttpRequestException("daemon offline"));

        // A new digest forces a probe, but the probe fails; the call must not throw and must keep the cached kind.
        var results = await service.ClassifyAsync([("llava", "sha256:new")]).ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Chat, results["llava"].Kind, "An offline probe falls back to the cached classification.");
    }

    [Test]
    public async Task ClassifyAsync_WhenDetectionThrowsAndNoCache_FallsBackToUnknown()
    {
        var (service, _, ollama) = CreateService();
        ollama.ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns<OllamaModelDetails>(_ => throw new HttpRequestException("daemon offline"));

        var results = await service.ClassifyAsync([("unseen", "sha256:x")]).ConfigureAwait(false);

        var result = results["unseen"];
        AssertEx.Equal(ModelKind.Unknown, result.Kind, "An unclassifiable, uncached model resolves to Unknown.");
        AssertEx.Empty(result.Capabilities, "An undetectable model has no capability badges.");
        AssertEx.False(result.IsOverridden, "An undetectable model has no override.");
    }

    [Test]
    public async Task SetOverrideAsync_PersistsOverrideAndReturnsEffectiveKind()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("qwen", "sha256:q", ModelKind.Chat, """["completion"]""").ConfigureAwait(false);

        var result = await service.SetOverrideAsync("qwen", ModelKind.Embedding).ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Embedding, result.Kind);
        AssertEx.True(result.IsOverridden, "Setting an override marks the model overridden.");
        var stored = AssertEx.NotNull(await store.GetByNameAsync("qwen").ConfigureAwait(false), "Override should persist.");
        AssertEx.Equal(ModelKind.Embedding, stored.OverrideKind);
        // Setting an override never probes the daemon.
        await ollama.DidNotReceive().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResetOverrideAsync_ClearsOverrideAndFallsBackToDetected()
    {
        var (service, store, ollama) = CreateService();
        _ = await store.UpsertDetectedAsync("codellama", "sha256:c", ModelKind.Chat, """["completion"]""").ConfigureAwait(false);
        _ = await store.SetOverrideAsync("codellama", ModelKind.Embedding).ConfigureAwait(false);

        var result = await service.ResetOverrideAsync("codellama").ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Chat, result.Kind, "Clearing the override falls back to the detected kind.");
        AssertEx.False(result.IsOverridden, "After reset the model is no longer overridden.");
        // The detected kind was already cached, so no re-probe is needed.
        await ollama.DidNotReceive().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResetOverrideAsync_WhenNoDetectionCached_DoesNotProbeAndLeavesDetectionToTheNextList()
    {
        var (service, store, ollama) = CreateService();
        // An override-only row (set before the model was ever probed) has Unknown detected and no cached capabilities.
        _ = await store.SetOverrideAsync("solar", ModelKind.Chat).ConfigureAwait(false);
        StubDetails(ollama, "solar", "embedding");

        // Reset clears the override and returns the cleared effective kind (Unknown) WITHOUT probing — probing here would
        // cache a null digest and force a redundant immediate re-probe on the next list.
        var result = await service.ResetOverrideAsync("solar").ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Unknown, result.Kind, "Reset returns the cleared detected kind (Unknown) without probing.");
        AssertEx.False(result.IsOverridden, "After reset the override is gone.");
        await ollama.DidNotReceive().ShowModelDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);

        // The next list lazily detects with the REAL live digest — exactly one probe across the whole reset+list flow.
        var listed = await service.ClassifyAsync([("solar", "sha256:live")]).ConfigureAwait(false);

        AssertEx.Equal(ModelKind.Embedding, listed["solar"].Kind, "The next list probes and surfaces the real detected kind.");
        await ollama.Received(1).ShowModelDetailsAsync("solar", Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static (IModelClassificationService Service, IModelClassificationStore Store, IOllamaModelService Ollama) CreateService()
    {
        var store = new InMemoryModelClassificationStore();
        var ollama = Substitute.For<IOllamaModelService>();
        var service = new ModelClassificationService(store, ollama, NullLogger<ModelClassificationService>.Instance);
        return (service, store, ollama);
    }

    private static void StubDetails(IOllamaModelService ollama, string modelName, params string[] capabilities)
    {
        // The service reads only OllamaModelDetails.Capabilities, so the ShowModelResponse payload can be empty.
        var details = new OllamaModelDetails(new ShowModelResponse(), MaxContextTokens: null, capabilities);
        ollama.ShowModelDetailsAsync(modelName, Arg.Any<CancellationToken>()).Returns(details);
    }

    /// <summary>
    ///     A faithful in-memory <see cref="IModelClassificationStore" /> used so the service tests exercise real
    ///     persistence semantics (override preservation on re-detect, NOCASE-style name keying) without a database.
    /// </summary>
    private sealed class InMemoryModelClassificationStore : IModelClassificationStore
    {
        private readonly Dictionary<string, ModelClassificationRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

        public Task<ModelClassificationRecord?> GetByNameAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_rows.TryGetValue(modelName, out var record) ? record : null);
        }

        public Task<IReadOnlyList<ModelClassificationRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModelClassificationRecord>>(_rows.Values.OrderBy(row => row.ModelName, StringComparer.Ordinal).ToArray());
        }

        public Task<ModelClassificationRecord> UpsertDetectedAsync(
            string modelName,
            string? digest,
            ModelKind detectedKind,
            string? capabilitiesJson,
            CancellationToken cancellationToken = default)
        {
            var existing = _rows.TryGetValue(modelName, out var current) ? current : null;
            var record = new ModelClassificationRecord(
                modelName,
                digest,
                detectedKind,
                capabilitiesJson,
                existing?.OverrideKind,
                DetectedAtUtc: 1,
                UpdatedAtUtc: 1);
            _rows[modelName] = record;
            return Task.FromResult(record);
        }

        public Task<ModelClassificationRecord> SetOverrideAsync(string modelName, ModelKind? overrideKind, CancellationToken cancellationToken = default)
        {
            var existing = _rows.TryGetValue(modelName, out var current) ? current : null;
            var record = existing is null
                ? new ModelClassificationRecord(modelName, Digest: null, ModelKind.Unknown, DetectedCapabilitiesJson: null, overrideKind, DetectedAtUtc: null, UpdatedAtUtc: 1)
                : existing with { OverrideKind = overrideKind, UpdatedAtUtc = 2 };
            _rows[modelName] = record;
            return Task.FromResult(record);
        }
    }
}
