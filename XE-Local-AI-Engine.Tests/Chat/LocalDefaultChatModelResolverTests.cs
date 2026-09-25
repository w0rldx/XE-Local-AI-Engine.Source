namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The local-default chat model resolver enumerates the installed GGUF (llama.cpp) models, excludes GGUFs whose
///     PERSISTED effective kind (OverrideKind ?? DetectedKind) is Embedding (no Ollama probe on this path), and applies
///     the pick order: the persisted node default iff it is an installed GGUF chat model, else the
///     most-recently-modified installed GGUF chat model (tie-break by name). Returns null when no chat model is
///     installed.
///     Production behaviour under test:
///     - A GGUF with NO persisted row (Unknown/absent) is eligible.
///     - A GGUF with DetectedKind=Embedding (and no override) is excluded.
///     - A GGUF with OverrideKind=Embedding overrides a Chat detected kind and is excluded.
///     - A GGUF with OverrideKind=Chat overrides an Embedding detected kind and is eligible.
///     - A model resident in a llama.cpp Chat-role process outranks all of the above, if it is an installed chat model.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalDefaultChatModelResolverTests
{
    [Test]
    public async Task ResolveAsync_WhenNoGgufInstalled_ReturnsNull()
    {
        var resolver = CreateResolver();

        var resolved = await resolver.ResolveAsync(persistedDefault: "qwen3.5:0.8b");

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenPersistedDefaultIsInstalledGgufChatModel_PrefersIt()
    {
        // The persisted node default wins (case-insensitive) when it is an installed GGUF chat model — short-circuiting
        // the most-recently-modified scan even though another model was modified later.
        var resolver = CreateResolver(Gguf("alpha:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("BravO:Q8_0", DateTimeOffset.UnixEpoch.AddDays(5)));

        var resolved = await resolver.ResolveAsync(persistedDefault: "bravo:Q8_0");

        AssertEx.Equal("BravO:Q8_0", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenPersistedDefaultNotInstalled_FallsBackToMostRecentlyModified()
    {
        // A stale persisted default (not an installed GGUF — e.g. a dead Ollama id) is ignored; the fallback is the
        // most-recently-modified installed GGUF chat model.
        var resolver = CreateResolver(Gguf("older:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1)),
            Gguf("newer:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)));

        var resolved = await resolver.ResolveAsync(persistedDefault: "qwen3.5:0.8b");

        AssertEx.Equal("newer:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenNoPersistedDefaultAndTie_BreaksByNameAscending()
    {
        // Same ModifiedAt → deterministic tie-break by name (case-insensitive ascending).
        var resolver = CreateResolver(Gguf("zeta:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("alpha:Q4_K_M", DateTimeOffset.UnixEpoch));

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

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

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("phi-4:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesGgufWithPersistedDetectedEmbeddingKind()
    {
        // An Embedding-classified GGUF (detected, no override) is not a chat model and must be excluded.
        ModelClassificationRecord[] classifications =
        [
            Classification("embed-model:Q4_K_M", ModelKind.Embedding, overrideKind: null),
            Classification("chat-model:Q4_K_M", ModelKind.Chat, overrideKind: null)
        ];
        LocalModelDescriptor[] installed =
        [
            Gguf("embed-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("chat-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1))
        ];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("chat-model:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesGgufWithEmbeddingOverrideEvenIfDetectedChat()
    {
        // OverrideKind=Embedding wins over DetectedKind=Chat — the effective kind is Embedding → excluded.
        ModelClassificationRecord[] classifications =
        [
            Classification("misclassified:Q4_K_M", ModelKind.Chat, ModelKind.Embedding)
        ];
        LocalModelDescriptor[] installed = [Gguf("misclassified:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_IncludesGgufWithChatOverrideEvenIfDetectedEmbedding()
    {
        // OverrideKind=Chat wins over DetectedKind=Embedding → eligible (operator corrected the classification).
        ModelClassificationRecord[] classifications =
        [
            Classification("corrected:Q4_K_M", ModelKind.Embedding, ModelKind.Chat)
        ];
        LocalModelDescriptor[] installed = [Gguf("corrected:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("corrected:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenOnlyInstalledGgufHasPersistedEmbeddingKind_ReturnsNull()
    {
        ModelClassificationRecord[] classifications =
        [
            Classification("embed-only:Q4_K_M", ModelKind.Embedding, overrideKind: null)
        ];
        LocalModelDescriptor[] installed = [Gguf("embed-only:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesEmbeddingNamedGgufWithNoPersistedRow()
    {
        // Belt-and-suspenders: a freshly-installed embedding GGUF has NO classification row, so the persisted-kind check
        // alone would leave it eligible. Its NAME (nomic-embed) must exclude it, leaving the chat GGUF as the default.
        LocalModelDescriptor[] installed =
        [
            Gguf("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("qwen2.5:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1))
        ];
        var resolver = CreateResolver([], installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("qwen2.5:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenOnlyInstalledGgufIsEmbeddingNamedWithNoRow_ReturnsNull()
    {
        LocalModelDescriptor[] installed = [Gguf("mxbai-embed-large:Q8_0", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver([], installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesGgufWithPersistedDetectedRerankerKind()
    {
        // A Reranker-classified GGUF (cross-encoder, no completion head) is not a chat model and must be excluded.
        ModelClassificationRecord[] classifications =
        [
            Classification("rerank-model:Q4_K_M", ModelKind.Reranker, overrideKind: null),
            Classification("chat-model:Q4_K_M", ModelKind.Chat, overrideKind: null)
        ];
        LocalModelDescriptor[] installed =
        [
            Gguf("rerank-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("chat-model:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1))
        ];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("chat-model:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_ExcludesRerankerNamedGgufWithNoPersistedRow()
    {
        // Belt-and-suspenders: a freshly-installed reranker GGUF has NO classification row. Its NAME (bge-reranker) must
        // exclude it even though it also matches the BGE- embedding prefix, leaving the chat GGUF as the default.
        LocalModelDescriptor[] installed =
        [
            Gguf("bge-reranker-v2-m3:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("qwen2.5:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(1))
        ];
        var resolver = CreateResolver([], installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("qwen2.5:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenOnlyInstalledGgufIsRerankerNamedWithNoRow_ReturnsNull()
    {
        LocalModelDescriptor[] installed = [Gguf("bge-reranker-large:Q8_0", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver([], installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Null(resolved);
    }

    [Test]
    public async Task ResolveAsync_IncludesEmbeddingNamedGgufWhenChatOverride()
    {
        // An explicit operator override to Chat wins over the name heuristic — the corrected model stays eligible.
        ModelClassificationRecord[] classifications =
        [
            Classification("nomic-embed-chat:Q4_K_M", ModelKind.Embedding, ModelKind.Chat)
        ];
        LocalModelDescriptor[] installed = [Gguf("nomic-embed-chat:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(classifications, installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("nomic-embed-chat:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenAChatModelIsResident_PrefersItOverThePersistedDefault()
    {
        // The tester's case: a big chat model is loaded, the node default is a different small one. Reusing the loaded
        // one avoids a second process the capacity gate would refuse.
        LocalModelDescriptor[] installed =
        [
            Gguf("bartowski/Small-GGUF:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("unsloth/Big-GGUF:UD-Q2_K_XL", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver([], installed, Resident("UNSLOTH/big-GGUF:UD-Q2_K_XL", ModelRole.Chat, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "bartowski/Small-GGUF:Q4_K_M");

        AssertEx.Equal("unsloth/Big-GGUF:UD-Q2_K_XL", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenTheResidentProcessIsNotChatRole_IgnoresIt()
    {
        // A chat-capable GGUF served only in an Embedding-role process does not count as a loaded chat model.
        LocalModelDescriptor[] installed =
        [
            Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("other:Q4_K_M", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver([], installed, Resident("other:Q4_K_M", ModelRole.Embedding, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "default:Q4_K_M");

        AssertEx.Equal("default:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenTheResidentModelIsNoLongerInstalled_IgnoresIt()
    {
        var resolver = CreateResolver([], [Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch)],
            Resident("deleted:Q4_K_M", ModelRole.Chat, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "default:Q4_K_M");

        AssertEx.Equal("default:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenTheResidentModelIsEmbeddingClassified_IgnoresIt()
    {
        // Same chat-capability notion as the non-resident pick: an Embedding-classified GGUF never becomes the chat default.
        ModelClassificationRecord[] classifications = [Classification("embed:Q4_K_M", ModelKind.Embedding, overrideKind: null)];
        LocalModelDescriptor[] installed =
        [
            Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("embed:Q4_K_M", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver(classifications, installed, Resident("embed:Q4_K_M", ModelRole.Chat, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "default:Q4_K_M");

        AssertEx.Equal("default:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenThePersistedDefaultIsAmongSeveralResident_PrefersIt()
    {
        LocalModelDescriptor[] installed =
        [
            Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("recent:Q4_K_M", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver([], installed,
            Resident("default:Q4_K_M", ModelRole.Chat, minutesAgo: 30),
            Resident("recent:Q4_K_M", ModelRole.Chat, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "default:Q4_K_M");

        AssertEx.Equal("default:Q4_K_M", resolved);
    }

    [Test]
    public async Task ResolveAsync_WhenSeveralAreResidentAndNoneIsTheDefault_PrefersTheMostRecentlyUsed()
    {
        LocalModelDescriptor[] installed =
        [
            Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("alpha:Q4_K_M", DateTimeOffset.UnixEpoch.AddDays(9)),
            Gguf("zeta:Q4_K_M", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver([], installed,
            Resident("alpha:Q4_K_M", ModelRole.Chat, minutesAgo: 30),
            Resident("zeta:Q4_K_M", ModelRole.Chat, minutesAgo: 1));

        var resolved = await resolver.ResolveAsync(persistedDefault: "default:Q4_K_M");

        AssertEx.Equal("zeta:Q4_K_M", resolved);
    }

    private static LocalDefaultChatModelResolver CreateResolver(params LocalModelDescriptor[] installed)
    {
        return CreateResolver([], installed);
    }

    private static LocalDefaultChatModelResolver CreateResolver(ModelClassificationRecord[] persistedClassifications,
        LocalModelDescriptor[] installed,
        params LlamaServerRunningProcess[] resident)
    {
        var ggufStore = Substitute.For<IGgufModelStore>();
        ggufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(installed));

        var classificationStore = Substitute.For<IModelClassificationStore>();
        classificationStore.ListAsync(Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult<IReadOnlyList<ModelClassificationRecord>>(persistedClassifications));

        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.ListRunningProcesses().Returns(resident);

        return new LocalDefaultChatModelResolver(ggufStore, classificationStore, supervisor);
    }

    private static LlamaServerRunningProcess Resident(string modelName, ModelRole role, int minutesAgo)
    {
        return new LlamaServerRunningProcess
        {
            ModelName = modelName,
            Role = role,
            LastUsedUtc = DateTimeOffset.UnixEpoch.AddDays(30).AddMinutes(-minutesAgo)
        };
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
        return new ModelClassificationRecord
        {
            ModelName = modelName,
            Digest = null,
            DetectedKind = detectedKind,
            DetectedCapabilitiesJson = null,
            OverrideKind = overrideKind,
            DetectedAtUtc = null,
            UpdatedAtUtc = 0L
        };
    }
}
