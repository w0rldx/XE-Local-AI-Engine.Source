namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single, code-grounded recommended embedding model for the knowledge base: Nomic's
///     <c>nomic-embed-text-v1.5</c>, served from the first-party <c>nomic-ai</c> GGUF packaging at <c>F16</c> (~274&#160;MB).
///     Without an embedding model installed, the knowledge base cannot index ANYTHING — ingestion fails at
///     <see cref="KnowledgeChunkEmbedder" /> with a content-free "not available" reason — so this exists to give a fresh
///     node a one-click way out, exactly as <see cref="RecommendedRerankerModel" /> does for the (strictly optional)
///     reranker. It resolves through the SAME operator HF download path as any other GGUF
///     (<see cref="IGgufModelStore.EnsureModelAsync" />), and the resulting name carries an <c>embed</c> fragment so
///     <c>ModelKindDetector.IsEmbeddingName</c> classifies it as <see cref="ModelKind.Embedding" /> and keeps it out of
///     the chat picker.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exact repository, and not merely "some embedding model".</b>
///         <c>KnowledgeEmbeddingVectorPolicy</c> applies the versioned Nomic Matryoshka-512 transform only when the
///         resolved model name contains <c>nomic-embed-text-v1.5</c> (<c>KnowledgeEmbeddingVectorPolicy.cs:146</c>);
///         every other model stays at its native width. Recommending the v1.5 repo therefore lands the node on the
///         vector policy the defaults (<see cref="KnowledgeBaseOptions.EmbeddingVectorMode" /> =
///         <see cref="KnowledgeEmbeddingVectorMode.Matryoshka512" />) were designed around. A v2 or a different
///         publisher would silently fall through to Native width — still functional, but not what the shipped defaults
///         assume.
///     </para>
///     <para>
///         <b>Why F16 rather than a small quant.</b> This is a ~137M-parameter model, so F16 is only ~274&#160;MB — the
///         quantization saving is worth a few hundred megabytes at most, while embedding quality degrades in a way that
///         is invisible at query time (retrieval silently returns worse chunks rather than failing). For a retrieval
///         backbone that every KB search depends on, the full-precision file is the right default. Live-verified on
///         2026-07-31: <c>nomic-ai/nomic-embed-text-v1.5-GGUF:F16</c> downloads, spawns an embedding
///         <c>llama-server</c>, indexes, and retrieves correctly.
///     </para>
///     <para>
///         The download request leaves <see cref="GgufModelRequest.Role" /> at <see cref="GgufRole.Embedding" /> so the
///         supervisor spawns it with the embedding-role flags (<c>--embeddings --pooling mean</c>) rather than the chat
///         ones. This is the one place the embedding recommendation legitimately differs from the reranker, which has no
///         role of its own and is identified by name.
///     </para>
/// </remarks>
public static class RecommendedEmbeddingModel
{
    /// <summary>Hugging Face repository hosting the GGUF-packaged embedding model.</summary>
    public const string RepoId = "nomic-ai/nomic-embed-text-v1.5-GGUF";

    /// <summary>Pinned quant — full precision, because the whole file is only ~274 MB (see the remarks).</summary>
    public const string Quant = "F16";

    /// <summary>
    ///     Canonical <c>{repoId}:{quant}</c> model name the download registers under. <c>F16</c> is not a Dynamic quant,
    ///     so the store resolves the request to this exact identity.
    /// </summary>
    public static string CanonicalModelName { get; } = GgufModelName.Format(RepoId, Quant);

    /// <summary>The download request the coordinator runs — repo + pinned quant, tagged with the embedding role.</summary>
    public static GgufModelRequest ToDownloadRequest()
    {
        return new GgufModelRequest
        {
            RepoId = RepoId,
            Quant = Quant,
            Role = GgufRole.Embedding
        };
    }

    /// <summary>
    ///     True when an installed model name IS the recommended embedding model — the canonical <c>{repo}:{quant}</c>
    ///     identity or any quant of the same repo (<c>{repo}:*</c>). Lets the endpoint report "already installed"
    ///     against the local registry without a network resolve.
    /// </summary>
    public static bool Matches(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return string.Equals(modelName, RepoId, StringComparison.OrdinalIgnoreCase)
               || modelName.StartsWith(RepoId + ":", StringComparison.OrdinalIgnoreCase);
    }
}
