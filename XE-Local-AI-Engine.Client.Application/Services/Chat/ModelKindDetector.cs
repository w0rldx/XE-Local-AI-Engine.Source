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
    private const string ThinkingCapability = "thinking";
    private const string ToolsCapability = "tools";

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

    /// <summary>
    ///     True when the model NAME alone identifies an embedding-only model (case-insensitive fragment/prefix match),
    ///     independent of any reported capabilities. Used where only a name is available (for example an installed GGUF
    ///     descriptor <c>&lt;repo&gt;:&lt;quant&gt;</c> carries no capability probe) to keep an embedding model out of the
    ///     chat surfaces and to auto-resolve it for knowledge-base embedding. Never guesses Chat.
    /// </summary>
    public static bool IsEmbeddingName(string modelName)
    {
        return FromNameHeuristic(modelName) == ModelKind.Embedding;
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise the <c>thinking</c> capability (case-insensitive). A
    ///     model without it returns HTTP 400 for any <c>think</c> request field, so the loopback path gates the field on
    ///     this. Null/empty capabilities (older daemon or offline) are treated as NOT thinking-capable — the safe choice
    ///     that avoids the 400 while still allowing a plain chat.
    /// </summary>
    public static bool SupportsThinking(IReadOnlyList<string>? capabilities)
    {
        return capabilities is { Count: > 0 } && ContainsCapability(capabilities, ThinkingCapability);
    }

    /// <summary>
    ///     True when the supplied Ollama capabilities advertise the <c>tools</c> capability (case-insensitive). A model
    ///     without it cannot drive tool calls, so the loopback path withholds all tool offers. Null/empty capabilities
    ///     are treated as NOT tool-capable (the safe default).
    /// </summary>
    public static bool SupportsTools(IReadOnlyList<string>? capabilities)
    {
        return capabilities is { Count: > 0 } && ContainsCapability(capabilities, ToolsCapability);
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
