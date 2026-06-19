namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Single source of truth for the GGUF registry-key convention: a model name is
///     <c>{repoId}[:{quant}]</c> (e.g. <c>bartowski/Llama-3.2-3B-Instruct-GGUF:Q4_K_M</c>). A Hugging Face repo id is
///     <c>org/name</c> and never contains a colon, so a trailing <c>:quant</c> unambiguously selects the quant. The
///     provider's <c>PullModelAsync</c>/<c>DeleteModelAsync</c> parse a bare model name through here; the store formats keys here.
/// </summary>
public static class GgufModelName
{
    /// <summary>Parses a <c>{repoId}[:{quant}]</c> model name into a request. A missing quant is left null for the store's default.</summary>
    public static GgufModelRequest Parse(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var separatorIndex = modelName.LastIndexOf(':');
        if (separatorIndex > 0 && separatorIndex < modelName.Length - 1)
        {
            return new GgufModelRequest
            {
                RepoId = modelName[..separatorIndex],
                Quant = modelName[(separatorIndex + 1)..]
            };
        }

        return new GgufModelRequest
        {
            RepoId = modelName
        };
    }

    /// <summary>Formats the canonical registry key for a repo + quant pair.</summary>
    public static string Format(string repoId, string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return $"{repoId}:{quant}";
    }
}
