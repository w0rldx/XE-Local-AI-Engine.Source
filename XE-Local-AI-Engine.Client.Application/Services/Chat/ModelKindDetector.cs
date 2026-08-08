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
    ///     Upper-cased name fragments that identify a reranker (cross-encoder) model. Rerankers score a
    ///     query/document pair rather than generate text, so they have no completion head and must never reach the chat
    ///     picker. Ollama advertises no distinct reranker capability token, so the NAME is the only reliable signal —
    ///     which is why the reranker check runs BEFORE the embedding check (a name such as <c>bge-reranker-v2-m3</c>
    ///     matches the <c>BGE-</c> embedding prefix too, and the reranker classification is the correct one).
    /// </summary>
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
        // A reranker exposes no distinct Ollama capability token, so its NAME is the only reliable signal even when
        // capabilities ARE reported (a cross-encoder can advertise an embedding capability). Check it first so a
        // reranker is never misclassified as Embedding or Chat.
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
    ///     True when the model NAME alone identifies an embedding-only model (case-insensitive fragment/prefix match),
    ///     independent of any reported capabilities. Used where only a name is available (for example an installed GGUF
    ///     descriptor <c>&lt;repo&gt;:&lt;quant&gt;</c> carries no capability probe) to keep an embedding model out of the
    ///     chat surfaces and to auto-resolve it for knowledge-base embedding. Never guesses Chat.
    /// </summary>
    public static bool IsEmbeddingName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Embedding;
    }

    /// <summary>
    ///     True when the model NAME alone identifies a reranker (cross-encoder) model (case-insensitive fragment match),
    ///     independent of any reported capabilities. Used where only a name is available (an installed GGUF descriptor
    ///     <c>&lt;repo&gt;:&lt;quant&gt;</c> carries no capability probe) to keep a reranker out of the chat surfaces and
    ///     tag it correctly in the model list. Takes precedence over <see cref="IsEmbeddingName" /> — a reranker name
    ///     that also matches an embedding prefix (for example <c>bge-reranker-…</c>) is a reranker, not an embedding.
    /// </summary>
    public static bool IsRerankerName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Reranker;
    }

    /// <summary>
    ///     True when the model NAME identifies a speculative-decoding draft model — its registry key carries the draft
    ///     quant marker (<c>…:MTP-Q8_0</c>) that <see cref="GgufDraftModel" /> stamps on a drafter at discovery/rescan.
    ///     Unlike the embedding/reranker fragments this is an exact structural marker, not a heuristic, so it cannot
    ///     misfire on a base model whose name merely mentions MTP (<c>unsloth/Qwen3.6-27B-MTP-GGUF:Q6_K</c>).
    /// </summary>
    public static bool IsDraftName(string modelName)
    {
        return GgufDraftModel.IsDraftModelName(modelName);
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise the <c>thinking</c> capability (case-insensitive). A
    ///     model without it returns HTTP 400 for any <c>think</c> request field, so the loopback path gates the field on
    ///     this. Null/empty capabilities (older daemon or offline) are treated as NOT thinking-capable — the safe choice
    ///     that avoids the 400 while still allowing a plain chat.
    /// </summary>
    public static bool SupportsThinking(IReadOnlyList<string>? capabilities)
    {
        return capabilities is { Count: > 0 } && ContainsCapability(capabilities, ThinkingCapability);
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise the <c>tools</c> capability (case-insensitive). A model
    ///     without it cannot drive tool calls, so the loopback path withholds all tool offers. Null/empty capabilities
    ///     are treated as NOT tool-capable (the safe default).
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
