namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Maps Ollama model capabilities (or a name heuristic when capabilities are absent) to a
///     provider-neutral <see cref="ModelKind" />. Pure and stateless so the classification service
///     can resolve detected kinds without any persistence or transport dependency.
/// </summary>
public static class ModelKindDetector
{
    private const string CompletionCapability = "completion";
    private const string EmbeddingCapability = "embedding";
    private const string ThinkingCapability = "thinking";
    private const string ToolsCapability = "tools";

    /// <summary>
    ///     Upper-cased name fragments that reliably identify embedding-only models when Ollama reports
    ///     no capabilities (older daemons or offline scenarios). Chat is never guessed from a name.
    /// </summary>
    private static readonly string[] EmbeddingNameFragments =
    [
        "EMBED",
        "ALL-MINILM",
        "NOMIC-EMBED",
        "MXBAI-EMBED"
    ];

    private static readonly string[] EmbeddingNamePrefixes =
    [
        "BGE-",
        "BGE:"
    ];

    /// <summary>
    ///     Upper-cased name fragments that identify a reranker (cross-encoder) model, which scores a query and
    ///     document pair rather than generating text and so must never reach the chat picker.
    /// </summary>
    /// <remarks>
    ///     Ollama advertises no distinct reranker capability token, so the NAME is the only reliable signal, and the
    ///     reranker check runs BEFORE the embedding one because a name like <c>bge-reranker-v2-m3</c> also matches
    ///     the <c>BGE-</c> embedding prefix while reranker is the correct classification.
    /// </remarks>
    private static readonly string[] RerankerNameFragments =
    [
        "RERANK"
    ];

    /// <summary>
    ///     Resolves the detected <see cref="ModelKind" /> from reported capabilities, falling back to a
    ///     conservative name heuristic when capabilities are unavailable.
    /// </summary>
    public static ModelKind FromCapabilities(IReadOnlyList<string>? capabilities, string modelName)
    {
        // A reranker exposes no distinct Ollama capability token, and a cross-encoder can advertise an embedding one,
        // so its NAME is the only reliable signal and is checked first.
        if (IsRerankerName(modelName))
        {
            return ModelKind.Reranker;
        }

        if (capabilities is { Count: > 0 })
        {
            var hasCompletion = ContainsCapability(capabilities, CompletionCapability);
            var hasEmbedding = ContainsCapability(capabilities, EmbeddingCapability);

            if (hasEmbedding && !hasCompletion)
            {
                return ModelKind.Embedding;
            }

            if (hasCompletion)
            {
                return ModelKind.Chat;
            }

            return ModelKind.Unknown;
        }

        return FromNameHeuristic(modelName);
    }

    /// <summary>
    ///     True when the model NAME alone identifies an embedding-only model, independent of reported capabilities.
    /// </summary>
    /// <remarks>
    ///     It is used where only a name is available — an installed GGUF descriptor carries no capability probe — to
    ///     keep an embedding model out of the chat surfaces and auto-resolve it for knowledge-base embedding. It
    ///     never guesses Chat.
    /// </remarks>
    public static bool IsEmbeddingName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Embedding;
    }

    /// <summary>
    ///     True when the model NAME alone identifies a reranker (cross-encoder) model, independent of reported
    ///     capabilities.
    /// </summary>
    /// <remarks>
    ///     It is used where only a name is available, to keep a reranker out of the chat surfaces and tag it in the
    ///     model list, and it takes precedence over <see cref="IsEmbeddingName" />: a name that matches both, such as
    ///     <c>bge-reranker-…</c>, is a reranker.
    /// </remarks>
    public static bool IsRerankerName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Reranker;
    }

    /// <summary>
    ///     True when the model NAME identifies a speculative-decoding draft model, its registry key carrying the
    ///     draft quant marker (<c>…:MTP-Q8_0</c>) <see cref="GgufDraftModel" /> stamps on a drafter at discovery.
    /// </summary>
    /// <remarks>
    ///     Unlike the embedding and reranker fragments this is an exact structural marker, not a heuristic, so it
    ///     cannot misfire on a base model whose name merely mentions MTP
    ///     (<c>unsloth/Qwen3.6-27B-MTP-GGUF:Q6_K</c>).
    /// </remarks>
    public static bool IsDraftName(string modelName)
    {
        return GgufDraftModel.IsDraftModelName(modelName);
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise <c>thinking</c>, which the loopback path gates the
    ///     <c>think</c> field on because a model without it answers HTTP 400.
    /// </summary>
    /// <remarks>
    ///     Null or empty capabilities, from an older daemon or an offline one, count as NOT thinking-capable: the
    ///     safe choice that avoids the 400 while still allowing a plain chat.
    /// </remarks>
    public static bool SupportsThinking(IReadOnlyList<string>? capabilities)
    {
        return capabilities is { Count: > 0 } && ContainsCapability(capabilities, ThinkingCapability);
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise <c>tools</c>; without it the loopback path withholds
    ///     every tool offer, and null or empty capabilities are NOT tool-capable, the safe default.
    /// </summary>
    public static bool SupportsTools(IReadOnlyList<string>? capabilities)
    {
        return capabilities is { Count: > 0 } && ContainsCapability(capabilities, ToolsCapability);
    }

    private static ModelKind FromNameHeuristic(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return ModelKind.Unknown;
        }

        var normalized = modelName.ToUpperInvariant();

        // Reranker wins over embedding: a name like BGE-RERANKER-… matches the BGE- embedding prefix too, and the
        // reranker classification is the correct one.
        if (RerankerNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            return ModelKind.Reranker;
        }

        var matchesEmbeddingName =
            EmbeddingNamePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
            || EmbeddingNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));

        return matchesEmbeddingName ? ModelKind.Embedding : ModelKind.Unknown;
    }

    private static bool ContainsCapability(IReadOnlyList<string> capabilities, string capability)
    {
        return capabilities.Any(value => string.Equals(value, capability, StringComparison.OrdinalIgnoreCase));
    }
}
