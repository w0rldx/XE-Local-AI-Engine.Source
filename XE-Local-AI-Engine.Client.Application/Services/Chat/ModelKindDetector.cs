namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Maps Ollama model capabilities (or a name heuristic when capabilities are absent) to a
///     provider-neutral <see cref="ModelKind" />. Pure and stateless so the classification service
///     can resolve detected kinds without any persistence or transport dependency.
/// </summary>
public static class ModelKindDetector
{
    private const string CompletionCapability = "completion";
    private const string EmbeddingCapability = "embedding";

    /// <summary>
    ///     Upper-cased name fragments that reliably identify embedding-only models when Ollama reports
    ///     no capabilities (older daemons or offline scenarios). Chat is never guessed from a name.
    /// </summary>
    private static readonly string[] EmbeddingNameFragments =
    [
        "EMBED",
        "ALL-MINILM",
        "NOMIC-EMBED",
        "MXBAI-EMBED"
    ];

    private static readonly string[] EmbeddingNamePrefixes =
    [
        "BGE-",
        "BGE:"
    ];

    /// <summary>
    ///     Resolves the detected <see cref="ModelKind" /> from reported capabilities, falling back to a
    ///     conservative name heuristic when capabilities are unavailable.
    /// </summary>
    public static ModelKind FromCapabilities(IReadOnlyList<string>? capabilities, string modelName)
    {
        if (capabilities is { Count: > 0 })
        {
            var hasCompletion = ContainsCapability(capabilities, CompletionCapability);
            var hasEmbedding = ContainsCapability(capabilities, EmbeddingCapability);

            if (hasEmbedding && !hasCompletion)
            {
                return ModelKind.Embedding;
            }

            if (hasCompletion)
            {
                return ModelKind.Chat;
            }

            return ModelKind.Unknown;
        }

        return FromNameHeuristic(modelName);
    }

    private static ModelKind FromNameHeuristic(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return ModelKind.Unknown;
        }

        var normalized = modelName.ToUpperInvariant();

        var matchesEmbeddingName =
            EmbeddingNamePrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal))
            || EmbeddingNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));

        return matchesEmbeddingName ? ModelKind.Embedding : ModelKind.Unknown;
    }

    private static bool ContainsCapability(IReadOnlyList<string> capabilities, string capability)
    {
        return capabilities.Any(value => string.Equals(value, capability, StringComparison.OrdinalIgnoreCase));
    }
}
