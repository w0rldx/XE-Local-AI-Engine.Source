namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single, code-grounded recommended embedding model for the knowledge base: Nomic's
///     <c>nomic-embed-text-v1.5</c>, from the first-party <c>nomic-ai</c> GGUF packaging at <c>F16</c> (~274&#160;MB).
/// </summary>
/// <remarks>
///     Without an embedding model installed the knowledge base cannot index ANYTHING — ingestion fails at
///     <see cref="KnowledgeChunkEmbedder" /> with a content-free "not available" reason — so this gives a fresh node a
///     one-click way out through the same operator HF download path as any other GGUF
///     (<see cref="IGgufModelStore.EnsureModelAsync" />). Why this repository, this quant and this role:
///     <c>docs/wiki/15-knowledge-base.md</c> ("Recommended embedding and reranker models").
/// </remarks>
public static class RecommendedEmbeddingModel
{
    /// <summary>Hugging Face repository hosting the GGUF-packaged embedding model.</summary>
    public const string RepoId = "nomic-ai/nomic-embed-text-v1.5-GGUF";

    /// <summary>Pinned quant — full precision, because the whole file is only ~274 MB.</summary>
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
    ///     The installed model that already makes this node able to embed, or <see langword="null" /> when none does.
    /// </summary>
    /// <remarks>
    ///     Reads the LOCAL registry only, with no network resolve: the recommended repo wins when present, else any
    ///     installed embedding-named model, ordered by name. Why that order, and why it is broader than
    ///     <see cref="RecommendedRerankerModel.ResolveExistingAsync" />: <c>docs/wiki/15-knowledge-base.md</c>
    ///     ("Recommended embedding and reranker models").
    /// </remarks>
    public static async Task<LocalModelDescriptor?> ResolveExistingAsync(IGgufModelStore modelStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStore);

        var installed = await modelStore.ListInstalledModelsAsync(cancellationToken);
        return installed.FirstOrDefault(model => Matches(model.ModelName))
               ?? installed
                  .Where(model => !string.IsNullOrWhiteSpace(model.ModelName)
                                  && ModelKindDetector.IsEmbeddingName(model.ModelName))
                  .OrderBy(model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                  .FirstOrDefault();
    }

    /// <summary>
    ///     True when an installed model name IS the recommended embedding model: the canonical <c>{repo}:{quant}</c>
    ///     identity, or any quant of the same repo (<c>{repo}:*</c>).
    /// </summary>
    /// <remarks>
    ///     Lets the endpoint report "already installed" against the local registry without a network resolve.
    /// </remarks>
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
