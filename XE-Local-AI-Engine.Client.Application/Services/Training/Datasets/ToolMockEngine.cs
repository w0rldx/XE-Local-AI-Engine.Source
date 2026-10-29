namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

public interface IToolMockEngine
{
    /// <summary>
    ///     Gets the literal response for a call; <see langword="false" /> when no rule matched and the body declares no
    ///     default. Never executes anything; never falls back to the real tool.
    /// </summary>
    bool TryRespond(ToolMockBodyV1 body, JsonElement arguments, [NotNullWhen(true)] out string? response);
}

/// <inheritdoc />
public sealed class ToolMockEngine : IToolMockEngine
{
    public bool TryRespond(ToolMockBodyV1 body, JsonElement arguments, [NotNullWhen(true)] out string? response)
    {
        ArgumentNullException.ThrowIfNull(body);
        var matched = body.Rules.FirstOrDefault(rule => Matches(rule, arguments));

        // No rule matched. The declared default is the ONLY fallback; there is deliberately no path back to real
        // execution, so an under-specified mock produces a visible validation-only outcome instead of a live tool call.
        response = matched is not null ? matched.Response : body.DefaultResponse;
        return response is not null;
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
