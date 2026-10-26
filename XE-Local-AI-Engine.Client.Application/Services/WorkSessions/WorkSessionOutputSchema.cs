namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;

/// <summary>
///     The flat object a work session must hand in on <c>complete_work_session</c> when its caller declared one: a
///     development-workflow Agent node's <c>outputSchema</c>, parsed and bounded by <c>DevWorkflowGraph</c>.
/// </summary>
/// <remarks>
///     Flat scalars only, so an edge condition's dot path can read every member, and so the tool's <c>output</c> grammar
///     stays one flat map whatever a node declares. The check is the shape, never the truth of what the model said.
/// </remarks>
internal sealed class WorkSessionOutputSchema
{
    public required IReadOnlyList<WorkSessionOutputProperty> Properties { get; init; }

    public WorkSessionOutputProperty? Find(string name) =>
        Properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));

    /// <summary>Why <paramref name="output" /> does not match, as a sentence the model can act on, or null when it does.</summary>
    public string? Violation(JsonElement? output)
    {
        if (output is not { ValueKind: JsonValueKind.Object } value)
        {
            return output is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
                ? $"This session has an output contract, so the call needs an 'output' object with {Members()}."
                : $"'output' must be a flat object with {Members()}, not a {output.Value.ValueKind}.";
        }

        foreach (var member in value.EnumerateObject())
        {
            if (Find(member.Name) is not { } property)
            {
                return $"'output' carries '{member.Name}', which the output contract does not declare; it allows {Members()}.";
            }

            if (Mismatch(property, member.Value) is { } mismatch)
            {
                return mismatch;
            }
        }

        return Properties.FirstOrDefault(property => property.Required && !value.TryGetProperty(property.Name, out _)) is { } missing
            ? $"'output' is missing the required member '{missing.Name}'."
            : null;
    }

    /// <summary>The objective's output-contract phase: one line per member, then what happens to a call without it.</summary>
    public string Render()
    {
        var text = new StringBuilder();
        _ = text.AppendLine("## Output contract")
                .AppendLine($"Finish by calling {WorkSessionToolDefinitions.CompleteWorkSession.ToolName} with an 'output' object: flat, these members and no others.");
        foreach (var property in Properties)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"- {property.Name}{(property.Required ? " (required)" : string.Empty)}: ")
                    .AppendLine(Describe(property));
        }

        return text.Append("A completion without a valid 'output' is refused and the session stays open until it carries one.").ToString();
    }

    private string Members() =>
        string.Join(", ", Properties.Select(static property => $"'{property.Name}'"));

    private static string Describe(WorkSessionOutputProperty property) =>
        property switch
        {
            { Enum: { } values } => $"one of {string.Join(", ", values.Select(static value => JsonSerializer.Serialize(value)))}",
            { Type: WorkSessionOutputType.String } => $"string, at most {property.MaxLength ?? WorkSessionToolDefinitions.TextMaxLength} characters",
            { Type: WorkSessionOutputType.Integer } => "a whole number",
            _ => JsonNamingPolicy.CamelCase.ConvertName(property.Type.ToString())
        };

    private static string? Mismatch(WorkSessionOutputProperty property, JsonElement value)
    {
        var fits = property.Type switch
        {
            WorkSessionOutputType.String => value.ValueKind == JsonValueKind.String,
            WorkSessionOutputType.Number => value.ValueKind == JsonValueKind.Number,
            // By value, not spelling: JSON Schema counts 1.0 and 1e0 as integers, and conditions compare numbers by value too.
            WorkSessionOutputType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
            _ => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        };
        if (!fits)
        {
            return $"'output.{property.Name}' must be {Describe(property)}, not {value.ValueKind}.";
        }

        if (property.Type != WorkSessionOutputType.String)
        {
            return null;
        }

        var text = value.GetString()!;
        if (property.Enum is { } values && !values.Contains(text, StringComparer.Ordinal))
        {
            return $"'output.{property.Name}' is {JsonSerializer.Serialize(text)}, which is not {Describe(property)}.";
        }

        var maxLength = property.MaxLength ?? WorkSessionToolDefinitions.TextMaxLength;
        return text.Length > maxLength ? $"'output.{property.Name}' is longer than {maxLength} characters." : null;
    }
}
