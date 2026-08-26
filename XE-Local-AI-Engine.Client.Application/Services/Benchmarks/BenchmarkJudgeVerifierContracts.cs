namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     The rubric-criterion vocabulary. <see cref="Llm" /> is what every criterion written before P2 carries by
///     default and is the only kind that costs a model turn; every other kind is checked server-side against the
///     graded answer with no inference at all.
/// </summary>
public static class BenchmarkJudgeCriterionKinds
{
    public const string Llm = "llm";
    public const string Exact = "exact";
    public const string Regex = "regex";
    public const string JsonSchema = "jsonSchema";
    public const string MathAnswer = "mathAnswer";
    public const string Constraint = "constraint";

    /// <summary>
    ///     Reserved for P3's <c>run_python</c> execution scoring. Named here so the vocabulary is complete and a
    ///     policy carrying it is refused with a specific reason instead of the generic "unknown kind"; P2 does not
    ///     implement it.
    /// </summary>
    public const string PythonTests = "pythonTests";

    /// <summary>Whether this kind is decided by <see cref="BenchmarkJudgeVerifiers" /> rather than by a model.</summary>
    public static bool IsVerifiable(string? kind) =>
        kind is Exact or Regex or JsonSchema or MathAnswer or Constraint;

    /// <summary>The kind a criterion carries, treating an absent value as the pre-P2 default.</summary>
    public static string Normalize(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? Llm : kind;
}

/// <summary>The judging modes a policy may name. Pointwise is the default and the only one P2 executes.</summary>
public static class BenchmarkJudgePolicyModes
{
    public const string Pointwise = "pointwise";
    public const string Pairwise = "pairwise";

    public static string Normalize(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? Pointwise : mode;
}

/// <summary>Text normalization applied before an <c>exact</c> comparison. Defaults trim and nothing else.</summary>
public sealed record BenchmarkVerifierNormalizeV1(
    bool Trim = true,
    bool CollapseWhitespace = false,
    bool CaseInsensitive = false,
    bool StripMarkdown = false);

/// <summary>IFEval-style structural constraints. <c>language</c> is deliberately absent (plan §2 #8).</summary>
public sealed record BenchmarkConstraintConfigV1(
    int? MinWords = null,
    int? MaxWords = null,
    IReadOnlyList<string>? MustContain = null,
    IReadOnlyList<string>? MustNotContain = null,
    string? Format = null)
{
    public const string FormatJson = "json";
    public const string FormatMarkdownList = "markdownList";
    public const string FormatNoMarkdown = "noMarkdown";
}

/// <summary>
///     One criterion's verifiable configuration, parsed and validated once. Produced by
///     <see cref="BenchmarkJudgeVerifierConfig.Parse" />, which BOTH the policy validator (at activation, discarding
///     the result) and <see cref="BenchmarkJudgeVerifiers" /> (at execution) call — a second parser is how an
///     activation-time check and a run-time check drift into disagreeing about the same config.
/// </summary>
public sealed record BenchmarkVerifierSpec
{
    public required string Kind { get; init; }
    public string? ExpectedText { get; init; }
    public BenchmarkVerifierNormalizeV1 Normalize { get; init; } = new();
    public Regex? Pattern { get; init; }
    public bool MustMatch { get; init; } = true;
    public JsonElement Schema { get; init; }
    public double ExpectedNumber { get; init; }
    public double RelativeTolerance { get; init; }
    public double AbsoluteTolerance { get; init; }
    public BenchmarkConstraintConfigV1? Constraint { get; init; }
}

/// <summary>
///     Parses and validates a criterion's <c>config</c> blob. Every failure is a
///     <see cref="BenchmarkJudgePolicyValidationException" /> so an operator saving an unusable rubric is told at
///     activation, not by a judging that fails an hour later (plan R5: a verifier that cannot run must never score 0).
/// </summary>
public static class BenchmarkJudgeVerifierConfig
{
    /// <summary>The longest regex pattern a policy may carry.</summary>
    public const int MaximumPatternLength = 512;

    /// <summary>How long one regex match may run before it is abandoned — belt beside <c>NonBacktracking</c>'s braces.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const double DefaultRelativeTolerance = 1e-6;

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    /// <summary>
    ///     The keywords <see cref="BenchmarkJudgeVerifiers" /> actually enforces. A schema naming anything else is
    ///     REFUSED at activation rather than accepted and silently under-checked — an operator who writes
    ///     <c>minLength</c> and is never told it does nothing has a criterion that passes answers it should fail.
    /// </summary>
    private static readonly string[] SupportedSchemaKeywords =
        ["type", "properties", "required", "items", "enum", "const", "additionalProperties"];

    private static readonly string[] SupportedSchemaTypes =
        ["object", "array", "string", "number", "integer", "boolean", "null"];

    /// <summary>
    ///     The parsed spec for a criterion, or <see langword="null" /> when the criterion is graded by the model.
    ///     Throws when the kind is unknown, reserved, or its config cannot be honoured.
    /// </summary>
    public static BenchmarkVerifierSpec? Parse(string? kind, string? configJson)
    {
        var resolved = BenchmarkJudgeCriterionKinds.Normalize(kind);
        if (string.Equals(resolved, BenchmarkJudgeCriterionKinds.Llm, StringComparison.Ordinal))
        {
            return configJson is null
                ? null
                : throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                    "An llm rubric criterion carries no configuration.");
        }

        if (string.Equals(resolved, BenchmarkJudgeCriterionKinds.PythonTests, StringComparison.Ordinal))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionKindUnsupported,
                "The pythonTests criterion kind is reserved and not available yet.");
        }

        if (!BenchmarkJudgeCriterionKinds.IsVerifiable(resolved))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionKindUnsupported,
                "The rubric criterion kind is not supported.");
        }

        if (string.IsNullOrWhiteSpace(configJson))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                "A verifiable rubric criterion requires a configuration.");
        }

        try
        {
            return resolved switch
            {
                BenchmarkJudgeCriterionKinds.Exact => ParseExact(configJson),
                BenchmarkJudgeCriterionKinds.Regex => ParseRegex(configJson),
                BenchmarkJudgeCriterionKinds.JsonSchema => ParseJsonSchema(configJson),
                BenchmarkJudgeCriterionKinds.MathAnswer => ParseMathAnswer(configJson),
                _ => ParseConstraint(configJson)
            };
        }
        catch (JsonException exception)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                "The rubric criterion configuration is not valid JSON for its kind.", exception);
        }
    }

    /// <summary>
    ///     The canonical form of a criterion's config, so two operators who typed the same rules with different key
    ///     order or whitespace produce the same policy hash. Called from the policy canonicalizer, which is the one
    ///     place the stored blob and the hash are both produced from.
    /// </summary>
    public static string? Canonicalize(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(configJson);
        return BenchmarkCanonicalJson.Serialize(document.RootElement);
    }

    private static BenchmarkVerifierSpec ParseExact(string configJson)
    {
        var config = JsonSerializer.Deserialize<ExactConfig>(configJson, ConfigOptions)
                     ?? throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "The exact criterion configuration is empty.");
        if (string.IsNullOrEmpty(config.Expected))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "An exact criterion requires the expected answer.");
        }

        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.Exact,
            ExpectedText = config.Expected,
            Normalize = config.Normalize ?? new BenchmarkVerifierNormalizeV1()
        };
    }

    private static BenchmarkVerifierSpec ParseRegex(string configJson)
    {
        var config = JsonSerializer.Deserialize<RegexConfig>(configJson, ConfigOptions)
                     ?? throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "The regex criterion configuration is empty.");
        if (string.IsNullOrEmpty(config.Pattern) || config.Pattern.Length > MaximumPatternLength)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                $"A regex criterion requires a pattern of at most {MaximumPatternLength} characters.");
        }

        Regex pattern;
        try
        {
            // NonBacktracking is the whole ReDoS answer: it runs in time linear in the input and REFUSES to compile
            // the constructs that make backtracking explode (backreferences, lookaround, atomic groups). Refusing such
            // a pattern here is cheaper and more honest than accepting it under a backtracking fallback and hoping the
            // timeout catches it.
            pattern = new Regex(config.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                "The regex pattern uses a construct that cannot be matched in linear time (backreferences, lookaround and atomic groups are refused).",
                exception);
        }

        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.Regex,
            Pattern = pattern,
            MustMatch = config.MustMatch
        };
    }

    private static BenchmarkVerifierSpec ParseJsonSchema(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schema", out var schema))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A jsonSchema criterion requires a schema.");
        }

        ValidateSchemaShape(schema);
        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.JsonSchema,
            Schema = schema.Clone()
        };
    }

    // ponytail: a structural subset of JSON Schema — type/properties/required/items/enum/const/additionalProperties —
    // rather than a validator dependency (plan §7.0 adds no NuGet). The ceiling is enforced rather than hidden: a
    // schema naming any other keyword is refused at activation, so the subset can never silently under-check. Upgrade
    // path is JsonSchema.Net behind this same seam.
    private static void ValidateSchemaShape(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A JSON schema must be an object.");
        }

        foreach (var member in schema.EnumerateObject())
        {
            if (!SupportedSchemaKeywords.Contains(member.Name, StringComparer.Ordinal))
            {
                throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                    $"The JSON schema keyword '{member.Name}' is not enforced by this build. Supported keywords: {string.Join(", ", SupportedSchemaKeywords)}.");
            }

            switch (member.Name)
            {
                case "type" when member.Value.ValueKind != JsonValueKind.String
                                 || !SupportedSchemaTypes.Contains(member.Value.GetString(), StringComparer.Ordinal):
                    throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                        $"A JSON schema type must be one of: {string.Join(", ", SupportedSchemaTypes)}.");
                case "properties" when member.Value.ValueKind != JsonValueKind.Object:
                    throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A JSON schema 'properties' must be an object.");
                case "properties":
                    foreach (var property in member.Value.EnumerateObject())
                    {
                        ValidateSchemaShape(property.Value);
                    }

                    break;
                case "items":
                    ValidateSchemaShape(member.Value);
                    break;
                case "required" when member.Value.ValueKind != JsonValueKind.Array
                                     || member.Value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String):
                    throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A JSON schema 'required' must be an array of names.");
                case "enum" when member.Value.ValueKind != JsonValueKind.Array || member.Value.GetArrayLength() == 0:
                    throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A JSON schema 'enum' must be a non-empty array.");
                case "additionalProperties" when member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False):
                    throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                        "A JSON schema 'additionalProperties' must be true or false in this build.");
                default:
                    break;
            }
        }
    }

    private static BenchmarkVerifierSpec ParseMathAnswer(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("expected", out var expected))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A mathAnswer criterion requires an expected value.");
        }

        var expectedText = expected.ValueKind switch
        {
            JsonValueKind.Number => expected.GetRawText(),
            JsonValueKind.String => expected.GetString(),
            _ => null
        };
        if (!BenchmarkMathAnswer.TryParseNumber(expectedText, out var expectedNumber))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                "A mathAnswer criterion's expected value must be a number, a fraction or a numeric string.");
        }

        var relative = ReadTolerance(document.RootElement, "relativeTolerance", DefaultRelativeTolerance);
        var absolute = ReadTolerance(document.RootElement, "absoluteTolerance", 0);
        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.MathAnswer,
            ExpectedNumber = expectedNumber,
            RelativeTolerance = relative,
            AbsoluteTolerance = absolute
        };
    }

    private static double ReadTolerance(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return fallback;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || value < 0 || !double.IsFinite(value))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, $"A mathAnswer '{name}' must be a finite number at or above zero.");
        }

        return value;
    }

    private static BenchmarkVerifierSpec ParseConstraint(string configJson)
    {
        var config = JsonSerializer.Deserialize<BenchmarkConstraintConfigV1>(configJson, ConfigOptions)
                     ?? throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "The constraint criterion configuration is empty.");
        if (config is { MinWords: null, MaxWords: null, MustContain: null, MustNotContain: null, Format: null })
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A constraint criterion must state at least one constraint.");
        }

        if (config.MinWords is < 0 || config.MaxWords is < 0 || (config.MinWords is { } min && config.MaxWords is { } max && min > max))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A constraint criterion's word bounds are inconsistent.");
        }

        if (config.MustContain?.Any(string.IsNullOrEmpty) == true || config.MustNotContain?.Any(string.IsNullOrEmpty) == true)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A constraint criterion's contains list carries an empty entry.");
        }

        if (config.Format is { } format
            && format is not (BenchmarkConstraintConfigV1.FormatJson
                or BenchmarkConstraintConfigV1.FormatMarkdownList
                or BenchmarkConstraintConfigV1.FormatNoMarkdown))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A constraint criterion's format is not supported.");
        }

        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.Constraint,
            Constraint = config
        };
    }

    private static BenchmarkJudgePolicyValidationException Invalid(string code, string message, Exception? inner = null) =>
        new(code, message)
        {
            Source = inner?.Source
        };

    private sealed record ExactConfig(string? Expected, BenchmarkVerifierNormalizeV1? Normalize);

    private sealed record RegexConfig(string? Pattern, bool MustMatch = true);
}

/// <summary>
///     Pulls the final numeric answer out of free-form model text. The extraction ORDER is the contract — a model that
///     shows its working leaves several numbers behind, and the last one it wrote is not reliably the one it meant.
/// </summary>
public static class BenchmarkMathAnswer
{
    /// <summary>The order tried, most explicit first. Documented here because a test pins it by name.</summary>
    public const string ExtractionOrder = "boxed, hash, phrase, last-number";

    private static readonly Regex HashMarker =
        new(@"####\s*(?<value>[^\r\n]{1,64})", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnswerPhrase =
        // The captured value keeps commas: they are thousands separators as often as punctuation, and Clean strips
        // both. Excluding them here read "$1,234,567" as 1.
        new(@"(?i:answer)\s*(?i:is)?\s*[:=]?\s*(?<value>[^\s;]{1,64})",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    private static readonly Regex AnyNumber =
        new(@"[-+]?[0-9][0-9,_]*(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?(?:\s*/\s*[-+]?[0-9][0-9,_]*(?:\.[0-9]+)?)?",
            RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

    private const string CurrencyAndNoise = "$€£¥%";

    /// <summary>
    ///     The answer this text states, and which rule found it. Returns <see langword="false" /> when no number can
    ///     be read at all — which is a FAILED criterion, never a zero-valued pass.
    /// </summary>
    public static bool TryExtract(string? text, out double value, out string source)
    {
        value = 0;
        source = "none";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryLastBoxed(text, out var boxed) && TryParseNumber(boxed, out value))
        {
            source = "boxed";
            return true;
        }

        if (TryLastMatch(HashMarker, text, out var hashed) && TryParseNumber(hashed, out value))
        {
            source = "hash";
            return true;
        }

        if (TryLastMatch(AnswerPhrase, text, out var phrased) && TryParseNumber(phrased, out value))
        {
            source = "phrase";
            return true;
        }

        if (TryLastMatch(AnyNumber, text, group: null, out var trailing) && TryParseNumber(trailing, out value))
        {
            source = "last-number";
            return true;
        }

        return false;
    }

    /// <summary>
    ///     A number as a model writes one: thousands separators, a currency symbol, trailing punctuation, scientific
    ///     notation, or a plain fraction such as <c>1/2</c> — which must compare equal to <c>0.5</c>.
    /// </summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = Clean(text);
        var slash = cleaned.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            if (!TryParseScalar(cleaned[..slash], out var numerator)
                || !TryParseScalar(cleaned[(slash + 1)..], out var denominator)
                || denominator == 0)
            {
                return false;
            }

            value = numerator / denominator;
            return double.IsFinite(value);
        }

        return TryParseScalar(cleaned, out value);
    }

    private static string Clean(string text) =>
        string.Concat(text.Where(static character => character is not (',' or '_' or ' ')
                                                     && !CurrencyAndNoise.Contains(character, StringComparison.Ordinal)))
              .Trim('.', ';', ':', ')', ']', '}', '*', '"', '\'');

    private static bool TryParseScalar(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>The LAST <c>\boxed{…}</c>, brace-matched so a nested <c>\frac{a}{b}</c> is read whole.</summary>
    private static bool TryLastBoxed(string text, out string content)
    {
        content = string.Empty;
        var found = false;
        var index = 0;
        while ((index = text.IndexOf(@"\boxed{", index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + 7;
            var depth = 1;
            var cursor = start;
            while (cursor < text.Length && depth > 0)
            {
                depth += text[cursor] switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0
                };
                cursor++;
            }

            if (depth == 0)
            {
                content = Unwrap(text[start..(cursor - 1)]);
                found = true;
            }

            index = start;
        }

        return found;
    }

    /// <summary>Reduces the LaTeX a boxed answer may wrap a number in to the number itself.</summary>
    private static string Unwrap(string boxed)
    {
        var trimmed = boxed.Trim();
        const string Frac = @"\frac{";
        if (!trimmed.StartsWith(Frac, StringComparison.Ordinal))
        {
            return trimmed.Replace(@"\!", string.Empty, StringComparison.Ordinal)
                          .Replace(@"\,", string.Empty, StringComparison.Ordinal);
        }

        var close = trimmed.IndexOf('}', Frac.Length);
        var second = close < 0 ? -1 : trimmed.IndexOf('{', close);
        var end = second < 0 ? -1 : trimmed.IndexOf('}', second);
        return close < 0 || second < 0 || end < 0
            ? trimmed
            : $"{trimmed[Frac.Length..close]}/{trimmed[(second + 1)..end]}";
    }

    private static bool TryLastMatch(Regex pattern, string text, out string content) =>
        TryLastMatch(pattern, text, "value", out content);

    private static bool TryLastMatch(Regex pattern, string text, string? group, out string content)
    {
        content = string.Empty;
        var found = false;
        foreach (var match in pattern.Matches(text).Cast<Match>())
        {
            content = group is null ? match.Value : match.Groups[group].Value;
            found = true;
        }

        return found;
    }
}
