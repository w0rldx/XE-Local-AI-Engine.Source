namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Text.Json.Serialization;

/// <summary>
///     Which scale a knowledge search hit's <see cref="KnowledgeSearchHit.Score" /> is on. One search yields one kind:
///     reranking is all-or-nothing per result.
/// </summary>
/// <remarks>
///     Serialized as the enum name everywhere, including the chat sources persisted in the message metadata blob, so
///     the stored form survives a reorder. Scores of different kinds are not comparable; neither is a probability.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeScoreKind>))]
public enum KnowledgeScoreKind
{
    /// <summary>The fused Reciprocal Rank Fusion score of the lexical and semantic arms (roughly 0.01 to 0.06).</summary>
    Fusion,

    /// <summary>The raw cross-encoder relevance the reranker reordered the hits by; unbounded, its range depends on the model.</summary>
    Rerank
}
