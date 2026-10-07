namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Whether a KL-divergence base is the same model as the quant it measures: KLD compares a quant against its own
///     model's reference logits, and llama-perplexity accepts any base with a compatible vocabulary.
/// </summary>
/// <remarks>
///     The freeze refuses a mismatched base and the fidelity executor applies the same rule again, because the base can
///     change after a run was frozen. Only header facts BOTH files carry are compared, so an unreadable header never refuses.
/// </remarks>
internal static class BenchmarkKldBaseMatch
{
    /// <summary>The message naming both models and how they differ, or <see langword="null" /> when they match.</summary>
    public static async Task<string?> DescribeMismatchAsync(IGgufModelStore ggufModels,
        string primaryModelName,
        string baseModelName,
        CancellationToken cancellationToken)
    {
        var primaryFacts = await ggufModels.ResolveModelFootprintFactsAsync(primaryModelName, cancellationToken);
        var baseFacts = await ggufModels.ResolveModelFootprintFactsAsync(baseModelName, cancellationToken);
        if (primaryFacts is null || baseFacts is null)
        {
            return null;
        }

        var differences = new List<string>(3);
        if (primaryFacts.Architecture is { } primaryArchitecture && baseFacts.Architecture is { } baseArchitecture
                                                                 && !string.Equals(primaryArchitecture, baseArchitecture, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"architecture {baseArchitecture} vs {primaryArchitecture}");
        }

        if (primaryFacts.BlockCount is { } primaryBlocks && baseFacts.BlockCount is { } baseBlocks && primaryBlocks != baseBlocks)
        {
            differences.Add($"{baseBlocks} vs {primaryBlocks} layers");
        }

        if (primaryFacts.EmbeddingLength is { } primaryWidth && baseFacts.EmbeddingLength is { } baseWidth && primaryWidth != baseWidth)
        {
            differences.Add($"embedding width {baseWidth} vs {primaryWidth}");
        }

        return differences.Count == 0
            ? null
            : $"The KL-divergence base model '{baseModelName}' is not the same model as '{primaryModelName}' "
              + $"({string.Join(", ", differences)}). KL divergence compares a quant against its own model; "
              + "choose a base of the same model in the project's fidelity settings.";
    }
}
