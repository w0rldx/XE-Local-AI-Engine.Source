namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Text.Json.Serialization;

/// <summary>Why a knowledge search answered with less than its full retrieval pipeline; serialized as the enum name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeSearchDegradation>))]
public enum KnowledgeSearchDegradation
{
    /// <summary>No query embedding was available, so only the lexical (BM25) arm ranked the hits.</summary>
    LexicalOnly,

    /// <summary>A reranker is configured but was cold, busy, failed or missed the latency budget; hits keep fusion order.</summary>
    RerankerUnavailable
}
