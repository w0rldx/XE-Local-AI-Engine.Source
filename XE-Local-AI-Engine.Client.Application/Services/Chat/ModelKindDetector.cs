namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Name rules for model kinds that a model's name reliably identifies. Pure and stateless, with no persistence
///     or transport dependency.
/// </summary>
public static class ModelKindDetector
{
    /// <summary>Upper-cased name fragments that reliably identify embedding-only models. Chat is never guessed from a name.</summary>
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
    ///     The NAME is the only reliable signal, and the reranker check runs BEFORE the embedding one because a name
    ///     like <c>bge-reranker-v2-m3</c> also matches the <c>BGE-</c> embedding prefix while reranker is correct.
    /// </remarks>
    private static readonly string[] RerankerNameFragments =
    [
        "RERANK"
    ];

    /// <summary>True when the model NAME alone identifies an embedding-only model.</summary>
    /// <remarks>
    ///     It is used to
    ///     keep an embedding model out of the chat surfaces and auto-resolve it for knowledge-base embedding. It
    ///     never guesses Chat.
    /// </remarks>
    public static bool IsEmbeddingName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Embedding;
    }

    /// <summary>True when the model NAME alone identifies a reranker (cross-encoder) model.</summary>
    /// <remarks>
    ///     It is used to keep a reranker out of the chat surfaces and tag it in the
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
}
