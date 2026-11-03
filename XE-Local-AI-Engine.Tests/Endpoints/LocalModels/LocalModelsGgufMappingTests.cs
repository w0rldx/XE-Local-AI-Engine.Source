namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat model list aggregates installed GGUF models (served by the bundled llama.cpp runtime, tagged
///     <see cref="LocalModelProviders.LlamaCpp" />) and classifies them <c>Chat</c> WITHOUT a runtime probe, so they
///     satisfy the React <c>kind === "Chat"</c> picker filter.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelsGgufMappingTests
{
    private static readonly Dictionary<string, ModelClassificationRecord> NoClassifications = new(StringComparer.OrdinalIgnoreCase);

    private static LocalModelDescriptor Gguf(string modelName,
        long? sizeBytes = 1024,
        bool isToolCapable = false,
        bool isReasoningCapable = false,
        IReadOnlyList<string>? capabilities = null,
        LocalModelOrigin? origin = null,
        string? modelContentFingerprint = null,
        bool reasoningBudgetEnforceable = true)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = LocalModelProviders.LlamaCpp,
            IsAvailable = true,
            SizeBytes = sizeBytes,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = null,
            Origin = origin,
            ModelContentFingerprint = modelContentFingerprint,
            IsToolCapable = isToolCapable,
            IsReasoningCapable = isReasoningCapable,
            ReasoningBudgetEnforceable = reasoningBudgetEnforceable,
            Capabilities = capabilities ?? []
        };
    }

    /// <summary>
    ///     The model list is where a client learns that a reasoning model's THINKING BUDGET does not apply. The flag is
    ///     detected from the same chat template as <c>IsReasoningCapable</c> and must survive the mapping unchanged in
    ///     both directions: a graded model whose template renders no reasoning end marker reports it false while
    ///     staying reasoning-capable, and one that does render an end marker keeps the enforceable default.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void ToLlamaCppModelResponses_CarriesReasoningBudgetEnforceabilityFromTheDescriptor(bool enforceable)
    {
        var response = LocalModelsMapper.ToLlamaCppModelResponses([
                Gguf("unsloth/qwen3.8-27b-GGUF:Q4_K_M", isReasoningCapable: true, reasoningBudgetEnforceable: enforceable)
            ],
            selectedModelName: null, NoClassifications);

        AssertEx.True(response[0].IsReasoningCapable, "enforceability never changes whether the model reasons");
        AssertEx.Equal(enforceable, response[0].ReasoningBudgetEnforceable);
    }

    /// <summary>An embedding or reranker GGUF generates no text, so its row carries no COMPLETION chip; a chat row keeps it.</summary>
    [Test]
    [Arguments("nomic-ai/nomic-embed-text-v1.5-GGUF:F16", "Embedding", "")]
    [Arguments("gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M", "Reranker", "")]
    [Arguments("unsloth/Qwen3.5-9B-GGUF:Q4_K_M", "Chat", "completion")]
    public void ToLlamaCppModelResponses_ListsCompletionOnlyForAModelThatGeneratesText(string modelName, string kind, string expected)
    {
        var response = LocalModelsMapper.ToLlamaCppModelResponses([Gguf(modelName, capabilities: ["completion"])], selectedModelName: null, NoClassifications);

        AssertEx.Equal(kind, response[0].Kind);
        AssertEx.Equal(expected, string.Join(",", response[0].Capabilities));
    }

    /// <summary>
    ///     The Models page renders family, parameter size and quant from the list response; a GGUF row showed "—" for
    ///     all three because the mapper dropped them. An undeclared value stays null, never a guess.
    /// </summary>
    [Test]
    public void ToLlamaCppModelResponses_CarriesFamilyParameterSizeAndQuantFromTheDescriptor()
    {
        var response = LocalModelsMapper.ToLlamaCppModelResponses([
                Gguf("bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M") with
                {
                    Family = "qwen2",
                    ParameterSize = "7B",
                    QuantizationLevel = "Q4_K_M"
                },
                Gguf("nomic-ai/nomic-embed-text-v1.5-GGUF:F16") with
                {
                    Family = "nomic-bert",
                    QuantizationLevel = "F16"
                }
            ],
            selectedModelName: null, NoClassifications);

        var declared = response.Single(static model => model.ModelName.StartsWith("bartowski", StringComparison.Ordinal));
        AssertEx.Equal("qwen2", declared.Family);
        AssertEx.Equal("7B", declared.ParameterSize);
        AssertEx.Equal("Q4_K_M", declared.QuantizationLevel);

        var undeclared = response.Single(static model => model.ModelName.StartsWith("nomic", StringComparison.Ordinal));
        AssertEx.True(undeclared.ParameterSize is null, "a GGUF without a size label reports no parameter size");
        AssertEx.Equal("nomic-bert", undeclared.Family);
    }

    /// <summary>
    ///     A descriptor whose template could not be read reports no reasoning at all, and the budget flag keeps its
    ///     inert TRUE default — a header that failed to parse is not evidence that a cap would fail to apply, and a
    ///     false here would read downstream as an instruction to drop one.
    /// </summary>
    [Test]
    public void ToLlamaCppModelResponses_UndetectedTemplate_KeepsTheInertEnforceableDefault()
    {
        var response = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M")],
            selectedModelName: null, NoClassifications);

        AssertEx.False(response[0].IsReasoningCapable);
        AssertEx.True(response[0].ReasoningBudgetEnforceable);
    }

    [Test]
    public void ToDetailsResponse_RunningModel_CarriesItsWindowAndPlacement_AndAColdOneCarriesNeither()
    {
        var running = Gguf("unsloth/Qwen3.6-35B-A3B-GGUF:UD-Q4_K_M").ToDetailsResponse("m", effectiveContextTokens: 65536, expertsOffloaded: true);
        var cold = Gguf("unsloth/Qwen3.6-35B-A3B-GGUF:UD-Q4_K_M").ToDetailsResponse("m");

        AssertEx.Equal(expected: 65536, running.EffectiveContextTokens!.Value);
        AssertEx.True(running.ExpertsOffloaded!.Value);
        AssertEx.Null(cold.EffectiveContextTokens);
        AssertEx.Null(cold.ExpertsOffloaded);
    }

    [Test]
    public void ToLlamaCppModelResponses_SurfacesOriginAndAggregateFingerprint()
    {
        const string fingerprint = "v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var response = LocalModelsMapper.ToLlamaCppModelResponses([
                Gguf("local-import:Q4_K_M", origin: LocalModelOrigin.Imported, modelContentFingerprint: fingerprint)
            ],
            selectedModelName: null, NoClassifications);

        AssertEx.Equal(LocalModelOrigin.Imported, response[0].Origin);
        AssertEx.Equal(fingerprint, response[0].ModelContentFingerprint);
    }

    [Test]
    public void ToLlamaCppModelResponses_TagsLlamaCpp_AndClassifiesChat()
    {
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M")],
            selectedModelName: null, NoClassifications);

        AssertEx.Equal(expected: 1, gguf.Count);
        AssertEx.Equal(LocalModelProviders.LlamaCpp, gguf[0].Provider);
        // Chat WITHOUT a capability probe — this is the crux for the React `kind === "Chat"` picker filter.
        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].Kind);
        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].DetectedKind);
        // A descriptor with no detected capabilities (unreadable / no chat template) stays the safe default.
        AssertEx.True(gguf[0].Capabilities.Count == 0, "no detected capabilities → empty");
        AssertEx.False(gguf[0].IsReasoningCapable, "no detected template → safe default false");
        AssertEx.False(gguf[0].IsToolCapable, "no detected template → safe default false");
        AssertEx.Equal(expected: 1024, gguf[0].SizeBytes);
    }

    [Test]
    public void ToLlamaCppModelResponses_ClassifiesEmbeddingNamedGgufAsEmbedding()
    {
        // A GGUF whose name identifies an embedding model (nomic-embed) is tagged Embedding so the React
        // `kind === "Chat"` picker filters it out — a fresh GGUF has no capability probe, only its name.
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M")],
            selectedModelName: null, NoClassifications);

        AssertEx.Equal(expected: 1, gguf.Count);
        AssertEx.Equal(ModelKind.Embedding.ToString(), gguf[0].Kind);
        AssertEx.Equal(ModelKind.Embedding.ToString(), gguf[0].DetectedKind);
    }

    [Test]
    public void ToLlamaCppModelResponses_ClassifiesRerankerNamedGgufAsReranker()
    {
        // A reranker (cross-encoder) GGUF name is tagged Reranker so the React `kind === "Chat"` picker filters it out.
        // The name matches the BGE- embedding prefix too, but reranker wins — it must NOT be tagged Embedding.
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("BAAI/bge-reranker-v2-m3-GGUF:Q4_K_M")],
            selectedModelName: null, NoClassifications);

        AssertEx.Equal(expected: 1, gguf.Count);
        AssertEx.Equal(ModelKind.Reranker.ToString(), gguf[0].Kind);
        AssertEx.Equal(ModelKind.Reranker.ToString(), gguf[0].DetectedKind);
    }

    [Test]
    public void ToLlamaCppModelResponses_ClassifiesChatNamedGgufAsChat()
    {
        // A non-embedding GGUF name stays Chat (the heuristic never guesses Chat from a name — it defaults to it).
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M")],
            selectedModelName: null, NoClassifications);

        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].Kind);
        AssertEx.Equal(ModelKind.Chat.ToString(), gguf[0].DetectedKind);
    }

    [Test]
    public void ToLlamaCppModelResponses_SurfacesDetectedCapabilities()
    {
        // A descriptor whose chat template was classified tool- and reasoning-capable must surface those flags + tokens
        // so the React chat UI enables the tools toggle and the graded reasoning-effort menu for the GGUF model.
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([
                Gguf("bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M",
                    isToolCapable: true,
                    isReasoningCapable: true,
                    capabilities: ["completion", "tools", "thinking"])
            ],
            selectedModelName: null, NoClassifications);

        AssertEx.True(gguf[0].IsToolCapable, "detected tool capability must surface");
        AssertEx.True(gguf[0].IsReasoningCapable, "detected reasoning capability must surface");
        AssertEx.Contains(gguf[0].Capabilities, static c => c == "tools");
        AssertEx.Contains(gguf[0].Capabilities, static c => c == "thinking");
    }

    [Test]
    public void ToLlamaCppModelResponses_MarksTheSelectedModel()
    {
        var gguf = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("repo-a:Q4_K_M"), Gguf("repo-b:Q4_K_M")],
            "repo-b:Q4_K_M",
            NoClassifications);

        AssertEx.ContainsSingle(gguf, static m => m.IsSelected);
        AssertEx.Contains(gguf, static m => m.ModelName == "repo-b:Q4_K_M" && m.IsSelected);
    }

    [Test]
    public void ToLlamaCppModelResponses_WhenOverridden_ReportsTheEffectiveAndDetectedKinds()
    {
        var classifications = new Dictionary<string, ModelClassificationRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["repo-a:Q4_K_M"] = Override("repo-a:Q4_K_M", ModelKind.Embedding)
        };

        var response = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("REPO-A:Q4_K_M")], selectedModelName: null, classifications);

        AssertEx.Equal(ModelKind.Embedding.ToString(), response[0].Kind);
        AssertEx.Equal(ModelKind.Chat.ToString(), response[0].DetectedKind);
        AssertEx.True(response[0].IsOverridden);
    }

    [Test]
    public void ToLlamaCppModelResponses_WhenARowHasNoOverride_ReportsTheOfflineKind()
    {
        var classifications = new Dictionary<string, ModelClassificationRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["repo-a:Q4_K_M"] = Override("repo-a:Q4_K_M", overrideKind: null)
        };

        var response = LocalModelsMapper.ToLlamaCppModelResponses([Gguf("repo-a:Q4_K_M")], selectedModelName: null, classifications);

        AssertEx.Equal(ModelKind.Chat.ToString(), response[0].Kind);
        AssertEx.False(response[0].IsOverridden);
    }

    [Test]
    public void ToListResponse_ConcatsGgufBeforeCloud()
    {
        var cloud = LocalModelsMapper.ToCodexCloudModelResponses(null);

        var response = LocalModelsMapper.ToListResponse("repo-a:Q4_K_M",
            "repo-a:Q4_K_M",
            new Dictionary<string, ModelClassificationRecord>(),
            cloud,
            [Gguf("repo-a:Q4_K_M")]);

        // Order: GGUF (llamacpp) → cloud (Codex).
        AssertEx.Equal(LocalModelProviders.LlamaCpp, response.Items[0].Provider);
        AssertEx.Equal("repo-a:Q4_K_M", response.Items[0].ModelName);
        AssertEx.True(response.Items.Skip(1).All(static m => m.Provider == LocalModelProviders.CodexOAuth),
            "the cloud group must remain appended last");
        AssertEx.Equal(1 + cloud.Count, response.Items.Count);
    }

    [Test]
    public void ToListResponse_WithNoGguf_IsEmpty()
    {
        var response = LocalModelsMapper.ToListResponse(selectedModelName: null,
            configuredDefaultModelName: null,
            new Dictionary<string, ModelClassificationRecord>());

        AssertEx.Empty(response.Items);
    }

    private static ModelClassificationRecord Override(string modelName, ModelKind? overrideKind)
    {
        return new ModelClassificationRecord
        {
            ModelName = modelName,
            Digest = null,
            DetectedKind = ModelKind.Unknown,
            DetectedCapabilitiesJson = null,
            OverrideKind = overrideKind,
            DetectedAtUtc = null,
            UpdatedAtUtc = 1
        };
    }
}
