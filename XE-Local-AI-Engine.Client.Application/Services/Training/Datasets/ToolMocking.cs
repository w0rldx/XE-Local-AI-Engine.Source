namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;

public interface IToolMockStaticVerifier
{
    /// <summary>
    ///     Statically verifies a mock body against the tool's parameter schema. Static only: no evaluation, no execution,
    ///     no network. A body that fails cannot be saved active, and there is no fallthrough to real execution.
    /// </summary>
    ToolMockVerificationV1 Verify(ToolMockBodyV1 body, string? parameterSchema);

    /// <summary>Parses raw mock JSON, returning a failing verdict instead of throwing on malformed input.</summary>
    bool TryParse(ReadOnlySpan<byte> mockJson, out ToolMockBodyV1? body, out string? failureReason);
}

public interface IToolMockEngine
{
    /// <summary>
    ///     The literal response for a call, or <see langword="null" /> when no rule matched and the body declares no
    ///     default. Never executes anything; never falls back to the real tool.
    /// </summary>
    string? TryRespond(ToolMockBodyV1 body, JsonElement arguments);
}

/// <inheritdoc />
public sealed class ToolMockStaticVerifier : IToolMockStaticVerifier
{
    /// <summary>
    ///     Markers of a value that would be interpreted rather than compared: template interpolation, shell/command
    ///     substitution, and spreadsheet-style formulas. A mock is declarative data, so any of these is a hard reject —
    ///     matching stays a string comparison and can never become an evaluation.
    /// </summary>
    private static readonly string[] ExpressionMarkers = ["{{", "}}", "${", "$(", "#{", "<%", "%>", "`"];

    public bool TryParse(ReadOnlySpan<byte> mockJson, out ToolMockBodyV1? body, out string? failureReason)
    {
        try
        {
            body = JsonSerializer.Deserialize<ToolMockBodyV1>(mockJson, TrainingJson.Options);
            failureReason = body is null ? "The mock body is empty." : null;
            return body is not null;
        }
        catch (JsonException exception)
        {
            body = null;
            failureReason = $"The mock body is not valid JSON: {exception.Message}";
            return false;
        }
    }

    public ToolMockVerificationV1 Verify(ToolMockBodyV1 body, string? parameterSchema)
    {
        ArgumentNullException.ThrowIfNull(body);
        var findings = new List<string>();

        if (body.SchemaVersion != 1)
        {
            findings.Add($"Unsupported mock schema version {body.SchemaVersion}.");
        }

        if (body.Rules.Count == 0 && string.IsNullOrEmpty(body.DefaultResponse))
        {
            findings.Add("A mock must declare at least one rule or a default response.");
        }

        if (body.Rules.Count > ToolMockBodyV1.MaxRules)
        {
            findings.Add($"A mock may declare at most {ToolMockBodyV1.MaxRules} rules.");
        }

        if (body.DefaultResponse is { Length: > ToolMockBodyV1.MaxResponseLength })
        {
            findings.Add("The default response exceeds the response size bound.");
        }

        var schemaFields = ReadSchemaFields(parameterSchema, findings);
        for (var index = 0; index < body.Rules.Count; index++)
        {
            VerifyRule(body.Rules[index], index, schemaFields, findings);
        }

        return new ToolMockVerificationV1(SchemaVersion: 1, findings.Count == 0, findings);
    }

    private static void VerifyRule(ToolMockRuleV1 rule, int index, IReadOnlySet<string>? schemaFields, List<string> findings)
    {
        var label = $"Rule {index}";
        if (rule is null)
        {
            findings.Add($"{label} is missing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            findings.Add($"{label} has no match field.");
        }
        else if (schemaFields is not null && !schemaFields.Contains(rule.Field))
        {
            findings.Add($"{label} matches on '{rule.Field}', which the tool's parameter schema does not declare.");
        }

        if (!Enum.IsDefined(rule.Match))
        {
            findings.Add($"{label} uses an unknown match kind.");
        }

        switch (rule.Match)
        {
            case ToolMockMatchKind.Equality when string.IsNullOrEmpty(rule.Value):
                findings.Add($"{label} is an equality match with no value.");
                break;
            case ToolMockMatchKind.Enum when rule.AnyOf is null || rule.AnyOf.Count == 0:
                findings.Add($"{label} is an enum match with no candidates.");
                break;
            default:
                break;
        }

        foreach (var value in Values(rule))
        {
            if (value.Length > ToolMockBodyV1.MaxValueLength)
            {
                findings.Add($"{label} has a match value longer than {ToolMockBodyV1.MaxValueLength} characters.");
            }

            if (IsExpressionLike(value))
            {
                findings.Add($"{label} has an expression-like match value; mock rules are literal comparisons only.");
            }
        }

        if (string.IsNullOrEmpty(rule.Response))
        {
            findings.Add($"{label} has no response.");
        }
        else if (rule.Response.Length > ToolMockBodyV1.MaxResponseLength)
        {
            findings.Add($"{label} has a response longer than {ToolMockBodyV1.MaxResponseLength} characters.");
        }
        else if (IsExpressionLike(rule.Response))
        {
            findings.Add($"{label} has an expression-like response; mock responses are literals only.");
        }
    }

    private static IEnumerable<string> Values(ToolMockRuleV1 rule)
    {
        if (rule.Value is not null)
        {
            yield return rule.Value;
        }

        foreach (var candidate in rule.AnyOf ?? [])
        {
            yield return candidate ?? string.Empty;
        }
    }

    private static bool IsExpressionLike(string value) =>
        value.StartsWith('=') || ExpressionMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));

    /// <summary>The tool schema's declared property names, or null when the tool declares no object schema to check against.</summary>
    private static IReadOnlySet<string>? ReadSchemaFields(string? parameterSchema, List<string> findings)
    {
        if (string.IsNullOrWhiteSpace(parameterSchema))
        {
            findings.Add("The tool declares no parameter schema, so match fields cannot be verified.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(parameterSchema);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            findings.Add($"The tool's parameter schema is not valid JSON: {exception.Message}");
            return null;
        }
    }
}

/// <inheritdoc />
public sealed class ToolMockEngine : IToolMockEngine
{
    public string? TryRespond(ToolMockBodyV1 body, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(body);
        var matched = body.Rules.FirstOrDefault(rule => Matches(rule, arguments));
        if (matched is not null)
        {
            return matched.Response;
        }

        // No rule matched. The declared default is the ONLY fallback; there is deliberately no path back to real
        // execution, so an under-specified mock produces a visible validation-only outcome instead of a live tool call.
        return body.DefaultResponse;
    }

    private static bool Matches(ToolMockRuleV1 rule, JsonElement arguments)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.Field) || arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!arguments.TryGetProperty(rule.Field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return rule.Match switch
        {
            ToolMockMatchKind.Presence => true,
            ToolMockMatchKind.Equality => string.Equals(Scalar(value), rule.Value, StringComparison.Ordinal),
            ToolMockMatchKind.Enum => rule.AnyOf?.Contains(Scalar(value), StringComparer.Ordinal) == true,
            _ => false
        };
    }

    private static string Scalar(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
