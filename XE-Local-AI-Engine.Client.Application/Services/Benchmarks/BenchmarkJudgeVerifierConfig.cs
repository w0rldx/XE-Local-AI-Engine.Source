namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;

/// <summary>
///     Parses and validates a criterion's <c>config</c> blob. Every failure is a
///     <see cref="BenchmarkJudgePolicyValidationException" /> so an operator saving an unusable rubric is told at
///     activation, not by a judging that fails later. A verifier that cannot run must never score 0.
/// </summary>
public static class BenchmarkJudgeVerifierConfig
{
    /// <summary>The longest regex pattern a policy may carry.</summary>
    public const int MaximumPatternLength = 512;

    /// <summary>How many symbols a pythonTests criterion may seed into the test namespace.</summary>
    public const int MaximumExports = 16;

    /// <summary>The ceiling a pythonTests criterion's own timeout is validated against; the node clamps it further.</summary>
    public const int MaximumTimeoutSeconds = 600;

    /// <summary>How long one regex match may run before it is abandoned — belt beside <c>NonBacktracking</c>'s braces.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const double DefaultRelativeTolerance = 1e-6;

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    /// <summary>The keywords <see cref="BenchmarkJudgeVerifiers" /> actually enforces.</summary>
    /// <remarks>
    ///     A schema naming anything else is REFUSED at activation rather than accepted and silently under-checked — an
    ///     operator who writes <c>minLength</c> and is never told it does nothing has a criterion that passes answers
    ///     it should fail.
    /// </remarks>
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
                BenchmarkJudgeCriterionKinds.PythonTests => ParsePythonTests(configJson),
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
    ///     order or whitespace produce the same policy hash.
    /// </summary>
    /// <remarks>
    ///     Called from the policy canonicalizer, which is the one place the stored blob and the hash are both produced
    ///     from.
    /// </remarks>
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
            // NonBacktracking is the whole ReDoS answer: linear in the input, and it REFUSES to compile the constructs that make backtracking explode (backreferences, lookaround, atomic groups).
            // Refusing such a pattern here is cheaper and more honest than accepting it under a backtracking fallback and hoping the timeout catches it.
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

    // simplified: a dependency-free structural subset of JSON Schema — type/properties/required/items/enum/const/additionalProperties. The ceiling is enforced, not hidden:
    // a schema naming any other keyword is refused at activation, so the subset can never silently under-check. A full validator can replace this behind the same seam.
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

    private static BenchmarkVerifierSpec ParsePythonTests(string configJson)
    {
        var config = JsonSerializer.Deserialize<BenchmarkPythonTestsConfigV1>(configJson, ConfigOptions)
                     ?? throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "The pythonTests criterion configuration is empty.");
        if (string.IsNullOrWhiteSpace(config.TestCode))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, "A pythonTests criterion requires the operator's test code.");
        }

        // Bounded at ACTIVATION rather than at judging: the composed harness has to fit inside the sandbox's script ceiling alongside the model's answer,
        // and an operator who learns that an hour into a batch learns it from a failed run instead of from the form they were filling in.
        if (config.TestCode.Length > BenchmarkPythonTestsHarness.TestCodeMaxChars)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                $"A pythonTests criterion's test code must be at most {BenchmarkPythonTestsHarness.TestCodeMaxChars} characters.");
        }

        // Exports are seeded into the test namespace as `solve(10)` shorthands for `candidate.solve(10)`, so each one has to be a name Python can bind.
        // Refusing here is what keeps the composed program's config a list of plain identifiers rather than something an operator can shape.
        var exports = config.Exports ?? [];
        if (exports.Count > MaximumExports)
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                $"A pythonTests criterion may name at most {MaximumExports} exports.");
        }

        if (exports.Any(static name => !IsIdentifier(name)))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                "A pythonTests criterion's exports must be plain Python identifiers.");
        }

        if (config.TimeoutSeconds is { } timeout && (timeout < 1 || timeout > MaximumTimeoutSeconds))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                $"A pythonTests criterion's timeoutSeconds must be between 1 and {MaximumTimeoutSeconds}.");
        }

        if (!BenchmarkPythonCodeExtraction.IsSupported(config.Extract))
        {
            throw Invalid(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid,
                $"A pythonTests criterion's extract must be '{BenchmarkPythonCodeExtraction.FirstPythonFence}' or '{BenchmarkPythonCodeExtraction.WholeText}'.");
        }

        return new BenchmarkVerifierSpec
        {
            Kind = BenchmarkJudgeCriterionKinds.PythonTests,
            PythonTests = config with
            {
                Exports = exports
            }
        };
    }

    private static bool IsIdentifier(string? name) =>
        !string.IsNullOrEmpty(name)
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(static character => char.IsLetterOrDigit(character) || character == '_');

    private static BenchmarkJudgePolicyValidationException Invalid(string code, string message, Exception? inner = null) =>
        new(code, message)
        {
            Source = inner?.Source
        };

    private sealed record ExactConfig(string? Expected, BenchmarkVerifierNormalizeV1? Normalize);

    private sealed record RegexConfig(string? Pattern, bool MustMatch = true);
}
