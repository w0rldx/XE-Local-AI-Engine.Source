namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single, code-grounded recommended cross-encoder reranker for the knowledge base: BAAI's multilingual
///     <c>bge-reranker-v2-m3</c>, served from the reputable <c>gpustack</c> GGUF packaging at the <c>Q4_K_M</c> quant
///     (~440&#160;MB — the quality/size sweet spot for a ~560M-param reranker). It resolves through the SAME operator HF
///     download path as any other GGUF (<see cref="IGgufModelStore.EnsureModelAsync" /> selects the file whose quant
///     matches <see cref="Quant" /> from the repo's <c>.gguf</c> list), and the resulting model name carries the
///     <c>reranker</c> fragment so <c>ModelKindDetector</c> classifies it as <see cref="ModelKind.Reranker" /> and keeps it
///     out of the chat picker.
/// </summary>
/// <remarks>
///     There is no reranker <see cref="GgufRole" /> — the process split only distinguishes chat/embedding — so the
///     download request leaves <see cref="GgufModelRequest.Role" /> at <see cref="GgufRole.Unknown" />; the reranker is
///     identified by name, not by a stored role hint.
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
    ///     True when an installed model name IS the recommended reranker — the canonical <c>{repo}:{quant}</c> identity or
    ///     any quant of the same repo (<c>{repo}:*</c>). Lets the endpoint report "already installed" against the local
    ///     registry without a network resolve.
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
