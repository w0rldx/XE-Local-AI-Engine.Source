namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

/// <summary>
///     Maps an advisor use-case to the Hugging Face free-text search terms that actually surface reputable,
///     fit-relevant GGUF models; the mapping only widens the candidate pool, it never excludes anything.
/// </summary>
/// <remarks>
///     The literal use-case word under-matches the Hub's substring search (verified live): <c>coding</c> misses
///     <c>Coder</c>/<c>code</c> models (Qwen-Coder, DeepSeek-Coder, Kimi-Code); <c>general</c> and <c>thinking</c>
///     surface niche finetunes; <c>vision</c> returns stale LLaVA repos while <c>vl</c> returns the current Qwen-VL
///     family. The advisor runs one trending search per term and merges the results; the publisher-trust signal and
///     the memory-fit ranking then pick the winners.
/// </remarks>
internal static class ModelFitUseCaseSearch
{
    // The fallback term for an empty/unknown use-case: instruct surfaces current general-purpose assistants
    // (Qwen-Instruct, etc.) far better than the raw trending list (which is dominated by niche/roleplay finetunes).
    private const string DefaultTerm = "instruct";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TermsByUseCase =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["general"] = ["instruct"],
            ["chat"] = ["instruct", "chat"],
            ["coding"] = ["coder", "code"],
            ["reasoning"] = ["reasoning", "instruct"],
            ["multimodal"] = ["vl", "vision"],
            ["embedding"] = ["embedding"],
        };

    /// <summary>Resolves the search terms for a use-case; the returned list is never empty.</summary>
    /// <remarks>
    ///     A known use-case returns its curated terms; an unknown or free-text use-case falls back to that text
    ///     verbatim, so a caller can still search anything; a null or blank use-case falls back to <c>instruct</c>.
    /// </remarks>
    public static IReadOnlyList<string> Resolve(string? useCase)
    {
        if (string.IsNullOrWhiteSpace(useCase))
        {
            return [DefaultTerm];
        }

        var key = useCase.Trim();
        return TermsByUseCase.TryGetValue(key, out var terms) ? terms : [key];
    }
}
