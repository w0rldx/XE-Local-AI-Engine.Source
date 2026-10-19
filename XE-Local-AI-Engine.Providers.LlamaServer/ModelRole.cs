namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Role a llama-server process serves; a distinct <c>(model, role)</c> pair is always a distinct process.
/// </summary>
/// <remarks>
///     Each role requires mutually exclusive launch flags: chat needs <c>--jinja</c>, embeddings need a
///     non-<c>none</c> pooling type to expose <c>/v1/embeddings</c>, and reranking needs <c>--rerank</c> with
///     <c>--pooling rank</c> to expose <c>/v1/rerank</c>, which excludes <c>--embeddings</c>. A decision model needs
///     no role flag at all: llama-server reads its <c>decision.type</c> header key. Each role-process counts against
///     the shared loaded-cap. Numeric values are persisted in receipts: append only.
/// </remarks>
public enum ModelRole
{
    /// <summary>Chat / tool-calling process launched with <c>--jinja</c>.</summary>
    Chat = 0,

    /// <summary>Embedding process launched with a non-<c>none</c> pooling type.</summary>
    Embedding = 1,

    /// <summary>Cross-encoder reranking process launched with <c>--rerank</c> + <c>--pooling rank</c> (serves <c>/v1/rerank</c>).</summary>
    Reranker = 2,

    /// <summary>
    ///     Native decision-model process (serves <c>/v1/systemone</c>): a pooled companion like the embedder and
    ///     reranker, launched with no role flag and <c>-b</c>/<c>-ub</c> pinned to its context, because the whole prompt
    ///     must fit one physical batch.
    /// </summary>
    Decision = 3
}
