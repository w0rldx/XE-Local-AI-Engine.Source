namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Runs one <c>llama-perplexity</c> child process to completion and returns what it printed.
///     <para>
///         A seam of its own rather than the training spawner: that one is Linux-only and scrubs a Python environment
///         this has no use for, while every platform that ships a llama.cpp runtime ships this tool. It exists mainly
///         so the parser can be tested against captured real output instead of a 27B model.
///     </para>
/// </summary>
public interface IBenchmarkPerplexityRunner
{
    Task<BenchmarkPerplexityProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <param name="Output">
///     stdout and stderr interleaved, bounded to the tail. llama.cpp prints its progress and its final estimate on
///     stderr, so splitting the streams would throw away the only line that matters.
/// </param>
public sealed record BenchmarkPerplexityProcessResult(int ExitCode, string Output);

public sealed class BenchmarkPerplexityRunner : IBenchmarkPerplexityRunner
{
    /// <summary>
    ///     How much of the child's output is retained. Enough to hold the whole summary block that follows the final
    ///     estimate, and small enough that an operator-visible error message can quote its tail.
    /// </summary>
    private const int MaximumOutputCharacters = 64 * 1024;

    public async Task<BenchmarkPerplexityProcessResult> RunAsync(string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        var output = new StringBuilder();
        var sink = new object();
        process.OutputDataReceived += (_, args) => Append(output, sink, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, sink, args.Data);

        if (!process.Start())
        {
            throw new BenchmarkExecutionException("The perplexity tool could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing the tree matters here beyond tidiness: a base-logit phase that keeps running would go on writing
            // to the temp file this invocation owns, and the caller is about to delete it.
            TryKill(process);
            throw;
        }

        // Drains the async readers, so the tail below is the whole tail rather than whatever had been flushed.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        lock (sink)
        {
            return new BenchmarkPerplexityProcessResult(process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder output, object sink, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            _ = output.AppendLine(line);
            if (output.Length > MaximumOutputCharacters)
            {
                _ = output.Remove(0, output.Length - MaximumOutputCharacters);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill. Nothing to clean up.
        }
        catch (SystemException)
        {
            // The OS refused the kill (permissions, a race with reaping). The invocation is failing either way.
        }
    }
}

/// <summary>
///     Reads the summary blocks <c>llama-perplexity</c> prints. Every pattern here was captured from the shipped
///     b10201 binary, not from upstream documentation, because the two disagree in three ways that each read as
///     "unparseable" rather than as an error:
///     <list type="bullet">
///         <item>a KL-divergence run prints NO <c>Final estimate</c> line at all — its perplexity is
///             <c>Mean PPL(Q)</c> inside the statistics block;</item>
///         <item>the statistics blocks separate a value from its error with <c>±</c>, while the plain perplexity
///             line uses <c>+/-</c>;</item>
///         <item>top-token agreement is printed as <c>Same top p</c>, not as any phrase containing "agreement".</item>
///     </list>
///     Fail-closed by construction: every method returns <see langword="null" /> when the expected line is absent, and
///     the caller turns that into a FAILED measurement rather than one with null numbers — "unmeasurable" and
///     "measured as nothing" are different facts, and only the second is a number.
/// </summary>
public static partial class BenchmarkPerplexityOutputParser
{
    /// <summary>
    ///     <c>Final estimate: PPL = 6.7983 +/- 0.07405</c>, printed by a plain perplexity run and by the KLD BASE
    ///     phase. Not anchored at line start: llama.cpp prefixes its output with a timestamped log marker, so an
    ///     anchored pattern matches nothing on a real run.
    /// </summary>
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

    /// <summary>
    ///     The perplexity a run reported, from whichever of the two shapes this invocation produced. A KLD run is
    ///     tried FIRST: it prints its perplexity inside the statistics block and no final-estimate line, so looking
    ///     for the plain shape first would find nothing and discard a measurement that succeeded.
    /// </summary>
    public static BenchmarkPerplexityReading? TryParsePerplexity(string? output)
    {
        if (output is null)
        {
            return null;
        }

        return Reading(MeanQuantPerplexityPattern, output) ?? Reading(FinalEstimatePattern, output);
    }

    /// <summary>
    ///     The KL-divergence block. The mean is required — it is the number the axis is about; the p99 and the
    ///     top-token agreement beside it are recorded when present and left null when a build stops printing them,
    ///     because a missing SECONDARY figure is not a reason to discard a measurement that did happen.
    /// </summary>
    public static BenchmarkKldReading? TryParseKld(string? output)
    {
        if (output is null || Value(MeanKldPattern, output) is not { } mean)
        {
            return null;
        }

        var agreement = Value(SameTopTokenPattern, output);
        return new BenchmarkKldReading(mean, Value(KldP99Pattern, output), agreement is { } percent ? percent / 100.0 : null);
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
            ? new BenchmarkPerplexityReading(mean, standardError)
            : null;
    }

    private static double? Value(Regex pattern, string output) =>
        pattern.Match(output) is { Success: true } match ? TryParseInvariant(match.Groups["value"].Value) : null;

    private static double? TryParseInvariant(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

public sealed record BenchmarkPerplexityReading(double Mean, double StandardError);

public sealed record BenchmarkKldReading(double Mean, double? P99, double? TopTokenAgreement);
