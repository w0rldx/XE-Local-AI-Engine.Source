namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single, code-grounded recommended embedding model for the knowledge base: Google's EmbeddingGemma&#160;2, from
///     the official <c>ggml-org</c> GGUF packaging at <c>Q8_0</c> (~310&#160;MB, weights only).
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
    public const string RepoId = "ggml-org/embeddinggemma-2-GGUF";

    /// <summary>Pinned quant: the repo ships only <c>Q8_0</c> and <c>BF16</c>, and <c>BF16</c> measured no retrieval gain.</summary>
    public const string Quant = "Q8_0";

    /// <summary>
    ///     Canonical <c>{repoId}:{quant}</c> model name the download registers under. <c>Q8_0</c> is not a Dynamic quant,
    ///     so the store resolves the request to this exact identity.
    /// </summary>
    public static string CanonicalModelName { get; } = GgufModelName.Format(RepoId, Quant);

    /// <summary>
    ///     The download request the coordinator runs: repo + pinned quant, tagged with the embedding role, weights only.
    /// </summary>
    /// <remarks>
    ///     Text embedding never loads the repo's <c>mmproj</c> projector, which is larger than the weights, so the
    ///     request opts out of the store's projector auto-pair.
    /// </remarks>
    public static GgufModelRequest ToDownloadRequest()
    {
        return new GgufModelRequest
        {
            RepoId = RepoId,
            Quant = Quant,
            Role = GgufRole.Embedding,
            IncludeProjector = false
        };
    }

    /// <summary>
    ///     The installed model that already makes this node able to embed, or <see langword="null" /> when none does.
    /// </summary>
    /// <remarks>
    ///     Reads the LOCAL registry only, with no network resolve, and picks through <see cref="PickInstalled" />, so the
    ///     reported model is the one an unpinned <see cref="IEmbeddingModelResolver" /> embeds with. Why it is broader than
    ///     <see cref="RecommendedRerankerModel.ResolveExistingAsync" />: <c>docs/wiki/15-knowledge-base.md</c>
    ///     ("Recommended embedding and reranker models").
    /// </remarks>
    public static async Task<LocalModelDescriptor?> ResolveExistingAsync(IGgufModelStore modelStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStore);

        return PickInstalled(await modelStore.ListInstalledModelsAsync(cancellationToken));
    }

    /// <summary>
    ///     The installed embedder an unpinned node uses, or <see langword="null" /> when none is installed: the model named
    ///     exactly <see cref="CanonicalModelName" />, else another quant of <see cref="RepoId" />, else any
    ///     <see cref="ModelKindDetector.IsEmbeddingName" /> model.
    /// </summary>
    /// <remarks>
    ///     Each step takes the first by ordinal-ignore-case name, then ordinal name, so the pick never depends on inventory
    ///     order, case-only variants included. Shared by <see cref="ResolveExistingAsync" /> and the resolver's unpinned
    ///     step so "already installed" and the embedding model always agree.
    /// </remarks>
    internal static LocalModelDescriptor? PickInstalled(IEnumerable<LocalModelDescriptor> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var ordered = installed
                      .Where(model => !string.IsNullOrWhiteSpace(model.ModelName))
                      .OrderBy(model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(model => model.ModelName, StringComparer.Ordinal)
                      .ToList();
        return ordered.FirstOrDefault(model => string.Equals(model.ModelName, CanonicalModelName, StringComparison.OrdinalIgnoreCase))
               ?? ordered.FirstOrDefault(model => Matches(model.ModelName))
               ?? ordered.FirstOrDefault(model => ModelKindDetector.IsEmbeddingName(model.ModelName));
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
