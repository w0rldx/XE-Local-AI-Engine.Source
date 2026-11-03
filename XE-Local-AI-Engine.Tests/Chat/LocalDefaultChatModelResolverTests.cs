namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The local-default chat model resolver picks among installed GGUF (llama.cpp) chat models: the persisted node
///     default iff it is one, else the most-recently-modified (tie-break by name); null when none is installed.
/// </summary>
/// <remarks>
///     A model resident in a llama.cpp Chat-role process outranks both, if it is an installed chat model.
///     Eligibility is the picker's rule (the override, else the name rules), with no runtime probe: a GGUF with no
///     persisted row is eligible unless its name says otherwise, and OverrideKind=Chat makes an Embedding name eligible.
/// </remarks>
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
        // A stale persisted default (not an installed GGUF — e.g. a removed model) is ignored; the fallback is the
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
        // A GGUF with no row in model_classifications (absent/Unknown) must stay eligible.
        // This is the normal state for a freshly-installed GGUF model.
        ModelClassificationRecord[] noRows = [];
        LocalModelDescriptor[] oneGguf = [Gguf("phi-4:Q4_K_M", DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver(noRows, oneGguf);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);

        AssertEx.Equal("phi-4:Q4_K_M", resolved);
    }

    /// <summary>
    ///     The default is always a model the picker lists, in the two cases where a row's stale or odd kind could split
    ///     them: an Unknown override hides a model from both, and a leftover DetectedKind counts for neither.
    /// </summary>
    [Test]
    [Arguments(ModelKind.Chat, ModelKind.Unknown, false)]
    [Arguments(ModelKind.Embedding, null, true)]
    public async Task ResolveAsync_AgreesWithThePicker(ModelKind detectedKind, ModelKind? overrideKind, bool isChat)
    {
        const string modelName = "neutral-name:Q4_K_M";
        var record = Classification(modelName, detectedKind, overrideKind);
        LocalModelDescriptor[] installed = [Gguf(modelName, DateTimeOffset.UnixEpoch)];
        var resolver = CreateResolver([record], installed);

        var resolved = await resolver.ResolveAsync(persistedDefault: null);
        var listed = LocalModelsMapper.ToLlamaCppModelResponses(installed, selectedModelName: null,
            new Dictionary<string, ModelClassificationRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [modelName] = record
            }).Single();

        AssertEx.Equal(isChat, string.Equals(listed.Kind, nameof(ModelKind.Chat), StringComparison.Ordinal));
        AssertEx.Equal(isChat, string.Equals(resolved, modelName, StringComparison.Ordinal));
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
    public async Task ResolveAsync_ExcludesEmbeddingNamedGgufWithNoPersistedRow()
    {
        // A freshly-installed embedding GGUF has NO classification row: its NAME (nomic-embed) excludes it, leaving the chat GGUF as the default.
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
    public async Task ResolveAsync_ExcludesRerankerNamedGgufWithNoPersistedRow()
    {
        // A freshly-installed reranker GGUF has NO classification row. Its NAME (bge-reranker) must exclude it even
        // though it also matches the BGE- embedding prefix, leaving the chat GGUF as the default.
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
        // A big chat model is loaded and the node default is a different small one. Reusing the loaded
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
        // Same chat-capability notion as the non-resident pick: an Embedding-overridden GGUF never becomes the chat default.
        ModelClassificationRecord[] classifications = [Classification("overridden:Q4_K_M", ModelKind.Chat, ModelKind.Embedding)];
        LocalModelDescriptor[] installed =
        [
            Gguf("default:Q4_K_M", DateTimeOffset.UnixEpoch),
            Gguf("overridden:Q4_K_M", DateTimeOffset.UnixEpoch)
        ];
        var resolver = CreateResolver(classifications, installed, Resident("overridden:Q4_K_M", ModelRole.Chat, minutesAgo: 1));

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
