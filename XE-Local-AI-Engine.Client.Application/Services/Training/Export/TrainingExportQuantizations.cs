namespace XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>
///     The quantizations a training export may produce.
/// </summary>
/// <remarks>
///     Deliberately a short allow-list rather than the whole quant ladder: the value is passed straight to
///     <c>llama-quantize</c> as a type argument, and the set below is what that tool accepts AND what the advisor
///     would ever recommend serving a fine-tune at. An unknown token would otherwise reach the subprocess and fail
///     late, after the merge has already cost minutes and gigabytes.
/// </remarks>
public static class TrainingExportQuantizations
{
    public const string Default = "Q4_K_M";

    /// <summary>The f16 intermediate every merged export passes through, and the only shape an adapter is emitted in.</summary>
    public const string Float16 = "F16";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Float16,
        "Q8_0",
        "Q6_K",
        "Q5_K_M",
        "Q5_K_S",
        "Q4_K_M",
        "Q4_K_S",
        "Q3_K_M"
    };

    public static IReadOnlyCollection<string> All => Allowed;

    /// <summary>Normalizes and validates a requested quantization, or returns null when it is not supported.</summary>
    public static string? TryNormalize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Default;
        }

        var normalized = requested.Trim().ToUpperInvariant();
        return Allowed.Contains(normalized) ? normalized : null;
    }
}
