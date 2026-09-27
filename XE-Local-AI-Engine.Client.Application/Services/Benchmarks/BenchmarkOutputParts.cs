namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;

/// <summary>Shaping for a run's output parts.</summary>
/// <remarks>
///     The live capture appends ONE part per stream delta, so a thinking model's turn arrives as thousands of
///     <c>{"kind":"reasoning","content":" 5"}</c> parts (measured: 476 KB of JSON for a 4.3k-token answer). Nothing
///     downstream wants that granularity: the terminal write stores the COALESCED form and the judge grades a
///     further-reduced projection of it. The part schema is unchanged either way — same kinds, same property names —
///     so every existing reader (the endpoint DTO, the live pane, the transcript viewer) is unaffected.
/// </remarks>
public static class BenchmarkOutputParts
{
    public const string OutputKind = "output";
    public const string ReasoningKind = "reasoning";

    public const string ToolCallKind = "tool_call";
    public const string ToolResultKind = "tool_result";

    /// <summary>Appended to the last text part the judge is shown when the answer had to be cut to fit its context.</summary>
    public const string TruncationMarker = "\n\n[truncated: the primary output exceeded the judge context budget]";

    // simplified: a coarse character allowance, not a second context budgeter — four chars per token mirrors HeuristicTokenEstimator's divisor, half the window left for the rest of the judge payload.
    // Ceiling: tool arguments and results are not counted, so a tool-heavy transcript can still overrun. Upgrade path: budget the BUILT payload with ITokenEstimator.
    private const int EstimatedCharsPerToken = 4;
    private const int MinimumJudgeTextChars = 2048;

    /// <summary>
    ///     Merges adjacent text parts of the same kind (output with output, reasoning with reasoning) into one part.
    /// </summary>
    /// <remarks>
    ///     Tool-call and tool-result parts pass through untouched and act as boundaries, so the transcript order is
    ///     preserved exactly — text before a tool call never merges with text after it.
    /// </remarks>
    public static IReadOnlyList<BenchmarkOutputPart> Coalesce(IEnumerable<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        List<BenchmarkOutputPart> merged = [];
        var text = new StringBuilder();
        string? pendingKind = null;
        foreach (var part in parts)
        {
            var isText = part.Kind is OutputKind or ReasoningKind;
            if (isText && string.Equals(pendingKind, part.Kind, StringComparison.Ordinal))
            {
                _ = text.Append(part.Content);
                continue;
            }

            if (pendingKind is not null)
            {
                merged.Add(new BenchmarkOutputPart(pendingKind, Content: text.ToString()));
                _ = text.Clear();
                pendingKind = null;
            }

            if (isText)
            {
                pendingKind = part.Kind;
                _ = text.Append(part.Content);
            }
            else
            {
                merged.Add(part);
            }
        }

        if (pendingKind is not null)
        {
            merged.Add(new BenchmarkOutputPart(pendingKind, Content: text.ToString()));
        }

        return merged;
    }

    /// <summary>
    ///     Whether any VISIBLE answer text was emitted — reasoning excluded, whitespace not counted. The narrow half of
    ///     <see cref="IsUnanswered" />, separate because a run cut off at the token budget is asked only this: did it
    ///     ever leave the scratchpad?
    /// </summary>
    public static bool HasAnswerText(IReadOnlyList<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Any(static part => string.Equals(part.Kind, OutputKind, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(part.Content));
    }

    /// <summary>Whether the turn produced no gradable answer, judged from the parts alone.</summary>
    /// <remarks>
    ///     Two shapes, both of which a provider reports as a CLEAN finish: the transcript ENDS on a <c>tool_call</c> (no
    ///     <c>tool_result</c> and no answer ever followed), or the reasoning-stripped text is empty or whitespace (a
    ///     thinking model spent the whole turn in its scratchpad). Either way the run reports <c>stop</c> or
    ///     <c>tool_calls</c>, which reads downstream as a finished answer: the judge grades an empty transcript and the
    ///     ranking seats the score beside runs that actually answered.
    /// </remarks>
    /// <param name="parts">
    ///     The COALESCED parts: a raw capture's last part is whatever fragment arrived last, not the shape of the turn.
    /// </param>
    public static bool IsUnanswered(IReadOnlyList<BenchmarkOutputPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (!HasAnswerText(parts))
        {
            return true;
        }

        // A tool_call as the FINAL part is by construction one no tool_result ever answered: the transcript is in turn
        // order, so nothing follows it. An earlier unmatched id is a provider quirk, not an unfinished turn.
        return parts.Count > 0 && string.Equals(parts[^1].Kind, ToolCallKind, StringComparison.Ordinal);
    }

    /// <summary>The parts the judge is shown: <see cref="Coalesce" />d, with every <c>reasoning</c> part DROPPED.</summary>
    /// <remarks>
    ///     Hidden chain-of-thought is not the graded answer — the rubric evaluates the visible output — and on a
    ///     thinking model the reasoning alone blew the judge context (measured: 107,192 estimated tokens against a
    ///     16,384 window, so every judging failed before inference). Text and tool parts are kept, in order. Text that
    ///     still cannot plausibly fit <paramref name="judgeContextTokens" /> is cut and marked with
    ///     <see cref="TruncationMarker" />; the cut applies to the judge's copy only, never the stored transcript.
    /// </remarks>
    public static IReadOnlyList<BenchmarkOutputPart> ForJudge(IEnumerable<BenchmarkOutputPart> parts, int judgeContextTokens)
    {
        var graded = Coalesce(parts)
                     .Where(static part => !string.Equals(part.Kind, ReasoningKind, StringComparison.Ordinal))
                     .ToArray();
        var allowance = Math.Max(MinimumJudgeTextChars, judgeContextTokens / 2 * EstimatedCharsPerToken);
        if (graded.Sum(static part => part.Content?.Length ?? 0) <= allowance)
        {
            return graded;
        }

        List<BenchmarkOutputPart> bounded = [];
        var remaining = allowance;
        foreach (var part in graded)
        {
            if (part.Content is not { } content)
            {
                bounded.Add(part);
                continue;
            }

            if (content.Length <= remaining)
            {
                bounded.Add(part);
                remaining -= content.Length;
                continue;
            }

            if (remaining > 0)
            {
                bounded.Add(part with
                {
                    Content = string.Concat(content.AsSpan(0, remaining), TruncationMarker)
                });
            }

            remaining = 0;
        }

        return bounded;
    }
}
