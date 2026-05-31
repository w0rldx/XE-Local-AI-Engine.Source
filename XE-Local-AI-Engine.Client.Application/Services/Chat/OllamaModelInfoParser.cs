namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
///     Represents ollama model info parser.
/// </summary>
public static partial class OllamaModelInfoParser
{
    public static bool TryGetContextLength(IDictionary<string, JsonElement> modelInfo, out int contextLength)
    {
        ArgumentNullException.ThrowIfNull(modelInfo);

        foreach (var (key, value) in modelInfo)
        {
            if (!ContextLengthKeyRegex().IsMatch(key))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var parsed)
                && parsed > 0)
            {
                contextLength = parsed;
                return true;
            }
        }

        contextLength = 0;
        return false;
    }

    public static bool TryGetContextLength(IDictionary<string, object>? modelInfo, out int contextLength)
    {
        if (modelInfo is null)
        {
            contextLength = 0;
            return false;
        }

        foreach (var (key, value) in modelInfo)
        {
            if (!ContextLengthKeyRegex().IsMatch(key))
            {
                continue;
            }

            if (TryReadPositiveInt32(value, out contextLength))
            {
                return true;
            }
        }

        contextLength = 0;
        return false;
    }

    private static bool TryReadPositiveInt32(object? value, out int contextLength)
    {
        switch (value)
        {
            case int parsed when parsed > 0:
                contextLength = parsed;
                return true;
            case long parsed when parsed is > 0 and <= int.MaxValue:
                contextLength = checked((int)parsed);
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element
                when element.TryGetInt32(out var parsed) && parsed > 0:
                contextLength = parsed;
                return true;
            default:
                contextLength = 0;
                return false;
        }
    }

    [GeneratedRegex(@"^[^.]+\.context_length$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextLengthKeyRegex();
}
