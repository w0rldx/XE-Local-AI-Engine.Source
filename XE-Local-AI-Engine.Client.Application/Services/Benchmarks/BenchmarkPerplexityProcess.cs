namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Reads the summary blocks <c>llama-perplexity</c> prints.</summary>
/// <remarks>
///     Every pattern here was captured from the shipped b10201 binary, not from upstream documentation, because the
///     two disagree in three ways that each read as "unparseable" rather than as an error — see
///     docs/wiki/20-benchmarks.md ("Quant fidelity — perplexity and KL divergence (display only)"). Fail-closed by
///     construction: every method returns <see langword="null" /> when the expected line is absent, and the caller
///     turns that into a FAILED measurement — "unmeasurable" and "measured as nothing" are different facts.
/// </remarks>
public static partial class BenchmarkPerplexityOutputParser
{
    /// <summary>
    ///     <c>Final estimate: PPL = 6.7983 +/- 0.07405</c>, printed by a plain perplexity run and by the KLD BASE phase.
    /// </summary>
    /// <remarks>
    ///     Not anchored at line start: llama.cpp prefixes its output with a timestamped log marker, so an anchored
    ///     pattern matches nothing on a real run.
    /// </remarks>
    [GeneratedRegex(@"Final estimate:\s*PPL\s*=\s*(?<mean>[0-9]+(?:\.[0-9]+)?)\s*\+/-\s*(?<error>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex FinalEstimatePattern { get; }

    /// <summary><c>Mean PPL(Q)                   :   5.886524 ±   0.398426</c> — the KLD run's perplexity.</summary>
    [GeneratedRegex(@"Mean\s+PPL\(Q\)\s*:\s*(?<mean>[0-9]+(?:\.[0-9]+)?)\s*\u00B1\s*(?<error>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex MeanQuantPerplexityPattern { get; }

    /// <summary><c>Mean    KLD:   0.030165 ±   0.002043</c>.</summary>
    [GeneratedRegex(@"Mean\s+KLD:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?(?:e[-+]?[0-9]+)?)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex MeanKldPattern { get; }

    /// <summary><c>99.0%   KLD:   0.388019</c>.</summary>
    [GeneratedRegex(@"99\.0%\s+KLD:\s*(?<value>-?[0-9]+(?:\.[0-9]+)?(?:e[-+]?[0-9]+)?)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex KldP99Pattern { get; }

    /// <summary>
    ///     <c>Same top p: 91.529 ± 0.780 %</c> — how often the quant's most likely token is the base's. Printed as a
    ///     percentage and stored as a 0..1 fraction, so a reader never has to know which of the two a column holds.
    /// </summary>
    [GeneratedRegex(@"Same\s+top\s+p\s*:\s*(?<value>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex SameTopTokenPattern { get; }

    /// <summary>The perplexity a run reported, from whichever of the two shapes this invocation produced.</summary>
    /// <remarks>
    ///     A KLD run is tried FIRST: it prints its perplexity inside the statistics block and no final-estimate line,
    ///     so looking for the plain shape first would find nothing and discard a measurement that succeeded.
    /// </remarks>
    public static BenchmarkPerplexityReading? TryParsePerplexity(string? output)
    {
        if (output is null)
        {
            return null;
        }

        return Reading(MeanQuantPerplexityPattern, output) ?? Reading(FinalEstimatePattern, output);
    }

    /// <summary>The KL-divergence block.</summary>
    /// <remarks>
    ///     The mean is required — it is the number the axis is about. The p99 and the top-token agreement beside it
    ///     are recorded when present and left null when a build stops printing them, because a missing SECONDARY
    ///     figure is not a reason to discard a measurement that did happen.
    /// </remarks>
    public static BenchmarkKldReading? TryParseKld(string? output)
    {
        if (output is null || Value(MeanKldPattern, output) is not { } mean)
        {
            return null;
        }

        var agreement = Value(SameTopTokenPattern, output);
        return new BenchmarkKldReading
        {
            Mean = mean,
            P99 = Value(KldP99Pattern, output),
            TopTokenAgreement = agreement is { } percent ? percent / 100.0 : null
        };
    }

    /// <summary>The last <paramref name="characters" /> of the child's output, for an operator-safe failure reason.</summary>
    public static string Tail(string? output, int characters = 1024)
    {
        var text = (output ?? string.Empty).TrimEnd();
        return text.Length <= characters ? text : text[^characters..];
    }

    private static BenchmarkPerplexityReading? Reading(Regex pattern, string output)
    {
        if (pattern.Match(output) is not { Success: true } match)
        {
            return null;
        }

        return TryParseInvariant(match.Groups["mean"].Value) is { } mean && TryParseInvariant(match.Groups["error"].Value) is { } standardError
            ? new BenchmarkPerplexityReading
            {
                Mean = mean,
                StandardError = standardError
            }
            : null;
    }

    private static double? Value(Regex pattern, string output) =>
        pattern.Match(output) is { Success: true } match ? TryParseInvariant(match.Groups["value"].Value) : null;

    private static double? TryParseInvariant(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed class BenchmarkPerplexityReading
{
    public required double Mean { get; init; }

    public required double StandardError { get; init; }
}

public sealed class BenchmarkKldReading
{
    public required double Mean { get; init; }

    public required double? P99 { get; init; }

    public required double? TopTokenAgreement { get; init; }
}
