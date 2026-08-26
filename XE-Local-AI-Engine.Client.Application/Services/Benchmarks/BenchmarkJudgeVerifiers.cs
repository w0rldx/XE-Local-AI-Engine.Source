namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     The server-side half of a rubric: every criterion whose kind is not <c>llm</c> is decided HERE, deterministically,
///     against the same graded projection the judge model would have been shown
///     (<see cref="BenchmarkOutputParts.ForJudge" />) — so the stored evidence answers questions about the same text the
///     rubric was applied to.
/// </summary>
/// <remarks>
///     <para>
///         Pure and side-effect free: no I/O, no clock, no randomness. Two runs of the same criterion against the same
///         answer produce the same verdict and the same detail string, which is what makes a verified score comparable
///         at all.
///     </para>
///     <para>
///         <b>Fail closed.</b> A verifier that cannot run — a config that got past the validator or a runtime
///         fault — throws, and the judging fails with a reason. It never returns "not passed", because 0 is a real
///         score an answer can earn and "unmeasurable" is not one.
///     </para>
/// </remarks>
public static class BenchmarkJudgeVerifiers
{
    /// <summary>
    ///     The most answer text any verifier reads. The graded projection is already bounded to the judge window, so
    ///     this is the second bound: a pathological transcript cannot turn a criterion check into a long CPU burn.
    /// </summary>
    public const int MaximumAnswerChars = 262_144;

    /// <summary>The score a verifiable criterion contributes; the rubric's own weights do the rest.</summary>
    public const int PassScore = BenchmarkJudgeOutputSchemaV2.MaximumCriterionScore;

    public const int FailScore = BenchmarkJudgeOutputSchemaV2.MinimumCriterionScore;

    private const int MaximumDetailChars = 512;

    private static readonly Regex MarkdownBullet =
        new(@"^\s{0,8}(?:[-*+]\s|\d{1,3}[.)]\s)", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly Regex MarkdownEmphasis =
        new(@"(?:\*\*|__|```|^#{1,6}\s)", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Multiline,
            TimeSpan.FromMilliseconds(250));

    /// <summary>The text every verifier reads: the graded parts' visible answer, coalesced and bounded.</summary>
    public static string AnswerText(IReadOnlyList<BenchmarkOutputPart> gradedParts)
    {
        ArgumentNullException.ThrowIfNull(gradedParts);
        var builder = new StringBuilder();
        foreach (var part in gradedParts)
        {
            if (string.Equals(part.Kind, BenchmarkOutputParts.OutputKind, StringComparison.Ordinal) && part.Content is { Length: > 0 } content)
            {
                _ = builder.Append(content);
            }
        }

        return builder.Length <= MaximumAnswerChars ? builder.ToString() : builder.ToString(0, MaximumAnswerChars);
    }

    /// <summary>
    ///     Decides one verifiable criterion. Throws <see cref="BenchmarkExecutionException" /> when the criterion is not
    ///     verifiable or its configuration cannot be honoured — never a silent "failed".
    /// </summary>
    public static BenchmarkJudgeVerifierResultV1 Verify(BenchmarkJudgeRubricCriterionV1 criterion, string answer)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(answer);
        var kind = BenchmarkJudgeCriterionKinds.Normalize(criterion.Kind);
        if (BenchmarkJudgeCriterionKinds.IsExecutionVerified(kind))
        {
            // Not decidable here by construction: this class is pure and synchronous, and pythonTests needs the
            // compute sandbox. Throwing rather than falling through to the constraint branch keeps a routing mistake a
            // failed judging instead of a criterion silently decided by the wrong verifier.
            throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' is decided by execution, not by a pure verifier.");
        }

        BenchmarkVerifierSpec spec;
        try
        {
            spec = BenchmarkJudgeVerifierConfig.Parse(kind, criterion.Config)
                   ?? throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' is graded by the judge model, not by a verifier.");
        }
        catch (BenchmarkJudgePolicyValidationException exception)
        {
            throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' cannot be verified: {exception.Message}")
            {
                Source = exception.Source
            };
        }

        var bounded = answer.Length <= MaximumAnswerChars ? answer : answer[..MaximumAnswerChars];
        try
        {
            var (passed, detail) = kind switch
            {
                BenchmarkJudgeCriterionKinds.Exact => VerifyExact(spec, bounded),
                BenchmarkJudgeCriterionKinds.Regex => VerifyRegex(spec, bounded),
                BenchmarkJudgeCriterionKinds.JsonSchema => VerifyJsonSchema(spec, bounded),
                BenchmarkJudgeCriterionKinds.MathAnswer => VerifyMathAnswer(spec, bounded),
                _ => VerifyConstraint(spec, bounded)
            };
            return new BenchmarkJudgeVerifierResultV1(criterion.Id, kind, passed, Bound(detail));
        }
        catch (RegexMatchTimeoutException exception)
        {
            // Practically unreachable under NonBacktracking, which is linear in the input — but a timeout is a
            // verifier that did not finish, so it fails the judging rather than reporting "not matched".
            throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' timed out while matching.")
            {
                Source = exception.Source
            };
        }
    }

    private static (bool Passed, string Detail) VerifyExact(BenchmarkVerifierSpec spec, string answer)
    {
        var expected = Normalize(spec.ExpectedText ?? string.Empty, spec.Normalize);
        var actual = Normalize(answer, spec.Normalize);
        var comparison = spec.Normalize.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(expected, actual, comparison)
            ? (true, "The normalized answer equals the expected text.")
            : (false, $"Expected '{Bound(expected, 120)}' but the normalized answer was '{Bound(actual, 120)}'.");
    }

    private static (bool Passed, string Detail) VerifyRegex(BenchmarkVerifierSpec spec, string answer)
    {
        var pattern = spec.Pattern ?? throw new BenchmarkExecutionException("The regex criterion has no compiled pattern.");
        var matched = pattern.IsMatch(answer);
        var detail = (spec.MustMatch, matched) switch
        {
            (true, true) => "The answer matches the required pattern.",
            (true, false) => "The answer does not match the required pattern.",
            (false, true) => "The answer matches the forbidden pattern.",
            _ => "The answer does not match the forbidden pattern."
        };
        return (matched == spec.MustMatch, detail);
    }

    private static (bool Passed, string Detail) VerifyJsonSchema(BenchmarkVerifierSpec spec, string answer)
    {
        var candidate = ExtractJson(answer);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidate);
        }
        catch (JsonException)
        {
            return (false, "The answer is not valid JSON.");
        }

        using (document)
        {
            var failure = SchemaFailure(spec.Schema, document.RootElement, "$");
            return failure is null ? (true, "The answer validates against the schema.") : (false, failure);
        }
    }

    private static (bool Passed, string Detail) VerifyMathAnswer(BenchmarkVerifierSpec spec, string answer)
    {
        if (!BenchmarkMathAnswer.TryExtract(answer, out var value, out var source))
        {
            return (false, $"No numeric answer could be read from the output (tried {BenchmarkMathAnswer.ExtractionOrder}).");
        }

        var tolerance = Math.Max(spec.AbsoluteTolerance, spec.RelativeTolerance * Math.Abs(spec.ExpectedNumber));
        var difference = Math.Abs(value - spec.ExpectedNumber);
        var expectedText = spec.ExpectedNumber.ToString("R", CultureInfo.InvariantCulture);
        var actualText = value.ToString("R", CultureInfo.InvariantCulture);
        return difference <= tolerance
            ? (true, $"Read {actualText} from the {source} rule; expected {expectedText}.")
            : (false, $"Read {actualText} from the {source} rule; expected {expectedText}.");
    }

    private static (bool Passed, string Detail) VerifyConstraint(BenchmarkVerifierSpec spec, string answer)
    {
        var config = spec.Constraint ?? throw new BenchmarkExecutionException("The constraint criterion has no configuration.");
        var words = answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (config.MinWords is { } minimum && words < minimum)
        {
            return (false, $"The answer has {words} words, fewer than the required {minimum}.");
        }

        if (config.MaxWords is { } maximum && words > maximum)
        {
            return (false, $"The answer has {words} words, more than the permitted {maximum}.");
        }

        if (config.MustContain?.FirstOrDefault(term => !answer.Contains(term, StringComparison.OrdinalIgnoreCase)) is { } missing)
        {
            return (false, $"The answer does not contain '{Bound(missing, 64)}'.");
        }

        if (config.MustNotContain?.FirstOrDefault(term => answer.Contains(term, StringComparison.OrdinalIgnoreCase)) is { } forbidden)
        {
            return (false, $"The answer contains the forbidden '{Bound(forbidden, 64)}'.");
        }

        return config.Format switch
        {
            BenchmarkConstraintConfigV1.FormatJson => IsJson(answer)
                ? (true, $"The answer is JSON and satisfies every constraint ({words} words).")
                : (false, "The answer is not valid JSON."),
            BenchmarkConstraintConfigV1.FormatMarkdownList => IsMarkdownList(answer)
                ? (true, $"The answer is a markdown list and satisfies every constraint ({words} words).")
                : (false, "The answer is not a markdown list."),
            BenchmarkConstraintConfigV1.FormatNoMarkdown => MarkdownEmphasis.IsMatch(answer) || IsMarkdownList(answer)
                ? (false, "The answer contains markdown formatting.")
                : (true, $"The answer is plain text and satisfies every constraint ({words} words)."),
            _ => (true, $"The answer satisfies every constraint ({words} words).")
        };
    }

    private static bool IsJson(string answer)
    {
        try
        {
            using var _ = JsonDocument.Parse(ExtractJson(answer));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsMarkdownList(string answer)
    {
        var lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && Array.TrueForAll(lines, static line => MarkdownBullet.IsMatch(line));
    }

    /// <summary>
    ///     The first fenced JSON block, or the whole trimmed answer — models fence a JSON answer more often than not.
    ///     Scanned rather than matched: the pattern that finds a fence needs a negative lookahead, which is exactly
    ///     what the linear-time engine every other pattern here uses refuses to compile.
    /// </summary>
    private static string ExtractJson(string answer)
    {
        const string Fence = "```";
        var open = answer.IndexOf(Fence, StringComparison.Ordinal);
        if (open < 0)
        {
            return answer.Trim();
        }

        var bodyStart = answer.IndexOf('\n', open);
        var close = bodyStart < 0 ? -1 : answer.IndexOf(Fence, bodyStart, StringComparison.Ordinal);
        return close < 0 ? answer.Trim() : answer[(bodyStart + 1)..close].Trim();
    }

    private static string Normalize(string value, BenchmarkVerifierNormalizeV1 options)
    {
        var text = value;
        if (options.StripMarkdown)
        {
            text = MarkdownEmphasis.Replace(text, string.Empty).Replace("`", string.Empty, StringComparison.Ordinal);
        }

        if (options.CollapseWhitespace)
        {
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return options.Trim ? text.Trim() : text;
    }

    // ponytail: the structural subset BenchmarkJudgeVerifierConfig already refuses anything outside of. Keeping the
    // enforced set and the accepted set in one file is what makes "accepted implies enforced" checkable.
    private static string? SchemaFailure(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            return $"{path} is not the required constant.";
        }

        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
        {
            return $"{path} is not one of the permitted values.";
        }

        if (schema.TryGetProperty("type", out var type) && type.GetString() is { } expectedType && !MatchesType(expectedType, value))
        {
            return $"{path} should be {expectedType} but is {value.ValueKind}.";
        }

        if (schema.TryGetProperty("required", out var required) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.GetString() is { } key && !value.TryGetProperty(key, out _))
                {
                    return $"{path} is missing the required property '{key}'.";
                }
            }
        }

        var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
        if (value.ValueKind == JsonValueKind.Object && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in value.EnumerateObject())
            {
                if (properties.TryGetProperty(member.Name, out var memberSchema))
                {
                    if (SchemaFailure(memberSchema, member.Value, $"{path}.{member.Name}") is { } failure)
                    {
                        return failure;
                    }
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                {
                    return $"{path} carries the unexpected property '{member.Name}'.";
                }
            }
        }

        if (schema.TryGetProperty("items", out var items) && value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (SchemaFailure(items, item, $"{path}[{index}]") is { } failure)
                {
                    return failure;
                }

                index++;
            }
        }

        return null;
    }

    private static bool MatchesType(string expected, JsonElement value) =>
        expected switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            _ => value.ValueKind == JsonValueKind.Number
        };

    private static string Bound(string value, int maximum = MaximumDetailChars) =>
        value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum), "…");
}
