namespace XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;

/// <summary>
///     Pulls the candidate program out of a free-form answer. The ORDER is the contract, because a model that writes
///     prose around two fenced blocks may otherwise have the wrong one taken: a <c>```python</c> fence first, then any
///     fence, then the whole trimmed text. The extracted text is stored in the attempt's verifier evidence, so a wrong
///     extraction is visible rather than silent — and a rubric that cannot tolerate the ambiguity constrains the answer
///     format with a <c>constraint</c> criterion, which is the real fix.
/// </summary>
internal static class BenchmarkPythonCodeExtraction
{
    /// <summary>Take the first Python-tagged fence, else the first fence, else the whole answer.</summary>
    public const string FirstPythonFence = "firstPythonFence";

    /// <summary>Take the answer verbatim. For rubrics whose prompt already demands bare code.</summary>
    public const string WholeText = "wholeText";

    /// <summary>The order tried, most explicit first. Documented here because a test pins it by name.</summary>
    public const string ExtractionOrder = "python-fence, any-fence, whole-text";

    public static bool IsSupported(string? mode) =>
        string.IsNullOrWhiteSpace(mode) || mode is FirstPythonFence or WholeText;

    public static string Extract(string answer, string? mode)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (string.Equals(mode, WholeText, StringComparison.Ordinal))
        {
            return answer.Trim();
        }

        if (TryFence(answer, requirePython: true, out var python))
        {
            return python;
        }

        return TryFence(answer, requirePython: false, out var any) ? any : answer.Trim();
    }

    /// <summary>
    ///     Scanned rather than matched: finding a fence whose info string is or is not <c>python</c> needs a negative
    ///     lookahead, and the linear-time regex engine every pattern in this area uses refuses to compile one.
    /// </summary>
    private static bool TryFence(string answer, bool requirePython, out string code)
    {
        const string Fence = "```";
        code = string.Empty;
        var cursor = 0;
        while (true)
        {
            var open = answer.IndexOf(Fence, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                return false;
            }

            var infoEnd = answer.IndexOf('\n', open);
            if (infoEnd < 0)
            {
                return false;
            }

            var info = answer[(open + Fence.Length)..infoEnd].Trim();
            var close = answer.IndexOf(Fence, infoEnd, StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            if (!requirePython || info.StartsWith("python", StringComparison.OrdinalIgnoreCase) || info.Equals("py", StringComparison.OrdinalIgnoreCase))
            {
                code = answer[(infoEnd + 1)..close].Trim();
                return code.Length > 0;
            }

            cursor = close + Fence.Length;
        }
    }
}
