namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single, code-grounded recommended cross-encoder reranker for the knowledge base: BAAI's multilingual
///     <c>bge-reranker-v2-m3</c>, from the <c>gpustack</c> GGUF packaging at the <c>Q4_K_M</c> quant (~440&#160;MB).
/// </summary>
/// <remarks>
///     It resolves through the same operator HF download path as any other GGUF
///     (<see cref="IGgufModelStore.EnsureModelAsync" /> picks the file whose quant matches <see cref="Quant" />). There is
///     no reranker <see cref="GgufRole" /> — the process split only distinguishes chat and embedding — so the request
///     leaves <see cref="GgufModelRequest.Role" /> at <see cref="GgufRole.Unknown" /> and the reranker is identified by
///     name: <c>docs/wiki/15-knowledge-base.md</c> ("Recommended embedding and reranker models").
/// </remarks>
public static class RecommendedRerankerModel
{
    /// <summary>Hugging Face repository hosting the GGUF-packaged reranker.</summary>
    public const string RepoId = "gpustack/bge-reranker-v2-m3-GGUF";

    /// <summary>Pinned quant — the quality/size sweet spot for this reranker.</summary>
    public const string Quant = "Q4_K_M";

    /// <summary>
    ///     Canonical <c>{repoId}:{quant}</c> model name the download registers under and the operator selects in Node
    ///     Settings to turn reranking on. <c>Q4_K_M</c> is not a Dynamic quant, so the store resolves the request to this
    ///     exact identity.
    /// </summary>
    public static string CanonicalModelName { get; } = GgufModelName.Format(RepoId, Quant);

    /// <summary>The download request the coordinator runs — repo + pinned quant, role left unknown (reranker is name-classified).</summary>
    public static GgufModelRequest ToDownloadRequest()
    {
        return new GgufModelRequest
        {
            RepoId = RepoId,
            Quant = Quant
        };
    }

    /// <summary>
    ///     The installed recommended reranker, or <see langword="null" /> when it is not present.
    /// </summary>
    /// <remarks>
    ///     Reads the LOCAL registry only, with no network resolve, and is deliberately NARROWER than
    ///     <see cref="RecommendedEmbeddingModel.ResolveExistingAsync" />: reranking is optional and choosing a reranker is
    ///     an explicit operator act, so only THIS repo counts as "already installed" — another installed reranker is not
    ///     one the operator asked for here.
    /// </remarks>
    public static async Task<LocalModelDescriptor?> ResolveExistingAsync(IGgufModelStore modelStore, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStore);

        var installed = await modelStore.ListInstalledModelsAsync(cancellationToken);
        return installed.FirstOrDefault(model => Matches(model.ModelName));
    }

    /// <summary>
    ///     True when an installed model name IS the recommended reranker: the canonical <c>{repo}:{quant}</c> identity,
    ///     or any quant of the same repo (<c>{repo}:*</c>).
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
