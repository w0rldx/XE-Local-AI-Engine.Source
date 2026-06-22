namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The local-default chat model resolver enumerates the installed GGUF (llama.cpp) models, excludes GGUFs whose
///     PERSISTED effective kind (OverrideKind ?? DetectedKind) is Embedding (no Ollama probe on this path), and applies
///     the pick order: the persisted node default iff it is an installed GGUF chat model, else the
///     most-recently-modified installed GGUF chat model (tie-break by name). Returns null when no chat model is
///     installed.
///
///     Production behaviour under test:
///     - A GGUF with NO persisted row (Unknown/absent) is eligible.
///     - A GGUF with DetectedKind=Embedding (and no override) is excluded.
///     - A GGUF with OverrideKind=Embedding overrides a Chat detected kind and is excluded.
///     - A GGUF with OverrideKind=Chat overrides an Embedding detected kind and is eligible.
/// </summary>
public sealed class LocalDefaultChatModelResolverTests
{
    [Test]
    public async Task ResolveAsync_WhenNoGgufInstalled_ReturnsNull()
    {
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(persistedDefault: "qwen3.5:0.8b").ConfigureAwait(false);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenPersistedDefaultIsInstalledGgufChatModel_PrefersIt()
    {
        // The persisted node default wins (case-insensitive) when it is an installed GGUF chat model — short-circuiting
        // the most-recently-modified scan even though another model was modified later.
        var resolver = CreateResolver(
            Gguf("alpha:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("BravO:Q8_0", DateTimeOffset.UnixEpoch.AddDays(5)));

        var resolved = await resolver.ResolveAsync(persistedDefault: "bravo:Q8_0").ConfigureAwait(false);

        AssertEx.Equal("BravO:Q8_0", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenPersistedDefaultNotInstalled_FallsBackToMostRecentlyModified()
    {
        // A stale persisted default (not an installed GGUF — e.g. a dead Ollama id) is ignored; the fallback is the
        // most-recently-modified installed GGUF chat model.
        var resolver = CreateResolver(
            Gguf("older:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1)),
            Gguf("newer:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)));

        var resolved = await resolver.ResolveAsync(persistedDefault: "qwen3.5:0.8b").ConfigureAwait(false);

        AssertEx.Equal("newer:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenNoPersistedDefaultAndTie_BreaksByNameAscending()
    {
        // Same ModifiedAt → deterministic tie-break by name (case-insensitive ascending).
        var resolver = CreateResolver(
            Gguf("zeta:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("alpha:Q4_K_M", DateTimeOffset.UnixEpoch));

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Equal("alpha:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenGgufHasNoPersistedRow_IsEligible()
    {
        // A GGUF with no row in model_classifications (absent/Unknown) must stay eligible — no Ollama probe is triggered.
        // This is the normal state for a freshly-installed GGUF model.
        ModelClassificationRecord[] noRows = [];
        LocalModelDescriptor[] oneGguf = [Gguf("phi-4:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(noRows, oneGguf);

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Equal("phi-4:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesGgufWithPersistedDetectedEmbeddingKind()
    {
        // An Embedding-classified GGUF (detected, no override) is not a chat model and must be excluded.
        ModelClassificationRecord[] classifications =
        [
            Classification("embed-model:Q4_K_M", detectedKind: ModelKind.Embedding, overrideKind: null),
            Classification("chat-model:Q4_K_M", detectedKind: ModelKind.Chat, overrideKind: null)
        ];
        LocalModelDescriptor[] installed =
        [
            Gguf("embed-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("chat-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1))
        ];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Equal("chat-model:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesGgufWithEmbeddingOverrideEvenIfDetectedChat()
    {
        // OverrideKind=Embedding wins over DetectedKind=Chat — the effective kind is Embedding → excluded.
        ModelClassificationRecord[] classifications =
        [
            Classification("misclassified:Q4_K_M", detectedKind: ModelKind.Chat, overrideKind: ModelKind.Embedding)
        ];
        LocalModelDescriptor[] installed = [Gguf("misclassified:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_IncludesGgufWithChatOverrideEvenIfDetectedEmbedding()
    {
        // OverrideKind=Chat wins over DetectedKind=Embedding → eligible (operator corrected the classification).
        ModelClassificationRecord[] classifications =
        [
            Classification("corrected:Q4_K_M", detectedKind: ModelKind.Embedding, overrideKind: ModelKind.Chat)
        ];
        LocalModelDescriptor[] installed = [Gguf("corrected:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Equal("corrected:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenOnlyInstalledGgufHasPersistedEmbeddingKind_ReturnsNull()
    {
        ModelClassificationRecord[] classifications =
        [
            Classification("embed-only:Q4_K_M", detectedKind: ModelKind.Embedding, overrideKind: null)
        ];
        LocalModelDescriptor[] installed = [Gguf("embed-only:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null).ConfigureAwait(false);

        AssertEx.Null(resolved);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static LocalDefaultChatModelResolver CreateResolver(params LocalModelDescriptor[] installed)
    {
        return CreateResolver([], installed);
    }

    private static LocalDefaultChatModelResolver CreateResolver(
        ModelClassificationRecord[] persistedClassifications,
        LocalModelDescriptor[] installed)
    {
        var ggufStore = Substitute.For<IGgufModelStore>();
        ggufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(installed));

        var classificationStore = Substitute.For<IModelClassificationStore>();
        classificationStore.ListAsync(Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult<IReadOnlyList<ModelClassificationRecord>>(persistedClassifications));

        return new LocalDefaultChatModelResolver(ggufStore, classificationStore);
    }

    private static LocalModelDescriptor Gguf(string modelName, DateTimeOffset modifiedAt)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = modifiedAt,
            MaxContextTokens = null,
            Capabilities = []
        };
    }

    private static ModelClassificationRecord Classification(string modelName, ModelKind detectedKind, ModelKind? overrideKind)
    {
        return new ModelClassificationRecord(
            ModelName: modelName,
            Digest: null,
            DetectedKind: detectedKind,
            DetectedCapabilitiesJson: null,
            OverrideKind: overrideKind,
            DetectedAtUtc: null,
            UpdatedAtUtc: 0L);
    }
}
