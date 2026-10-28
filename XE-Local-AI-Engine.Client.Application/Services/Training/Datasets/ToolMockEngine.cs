namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;

public interface IToolMockEngine
{
    /// <summary>
    ///     The literal response for a call, or <see langword="null" /> when no rule matched and the body declares no
    ///     default. Never executes anything; never falls back to the real tool.
    /// </summary>
    string? TryRespond(ToolMockBodyV1 body, JsonElement arguments);
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
