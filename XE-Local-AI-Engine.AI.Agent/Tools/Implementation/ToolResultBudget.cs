namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
///     Shared truncation for tool results at the function-invocation boundary. A tool result becomes part of the chat
///     history and is re-sent to the model on every subsequent turn, so an unbounded result is unbounded input for the
///     rest of the conversation. This clips the textual payload to a character budget and appends an explicit marker so
///     the model can tell the output was cut. Non-text content (for example an image data block) is left intact.
/// </summary>
internal static class ToolResultBudget
{
    /// <summary>
    ///     Returns <paramref name="result" /> unchanged when it is within budget, otherwise a truncated equivalent.
    ///     Handles the concrete shapes a tool can return through the Microsoft.Extensions.AI pipeline: a raw
    ///     <see cref="string" /> (ClientLocal handlers), a single <see cref="TextContent" />, a <see cref="JsonElement" />
    ///     (structured MCP result), or an <see cref="AIContent" /> array (multi-block MCP result).
    /// </summary>
    public static object? Apply(object? result, int maxCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        switch (result)
        {
            case null:
                return null;
            case string text:
                return Truncate(text, maxCharacters);
            case TextContent textContent:
                return textContent.Text.Length <= maxCharacters
                    ? textContent
                    : new TextContent(Truncate(textContent.Text, maxCharacters));
            case JsonElement json:
            {
                var raw = json.GetRawText();
                return raw.Length <= maxCharacters ? result : Truncate(raw, maxCharacters);
            }
            case AIContent[] parts:
                return TruncateContentParts(parts, maxCharacters);
            default:
                return result;
        }
    }

    /// <summary>Clips <paramref name="text" /> to <paramref name="maxCharacters" /> and appends a truncation marker.</summary>
    public static string Truncate(string text, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var shown = text.AsSpan(0, maxCharacters).ToString();
        return string.Create(CultureInfo.InvariantCulture, $"{shown}\n\n[truncated: {maxCharacters} of {text.Length} chars shown]");
    }

    private static object TruncateContentParts(AIContent[] parts, int maxCharacters)
    {
        var totalTextLength = 0;
        foreach (var part in parts)
        {
            if (part is TextContent text)
            {
                totalTextLength += text.Text.Length;
            }
        }

        if (totalTextLength <= maxCharacters)
        {
            return parts;
        }

        // Over budget: collapse every text block into a single truncated block (keeping any non-text blocks in place),
        // so the model still sees the highest-priority prefix plus an explicit marker instead of the whole payload.
        var combined = string.Concat(parts.OfType<TextContent>().Select(static text => text.Text));
        var truncatedText = Truncate(combined, maxCharacters);

        var rebuilt = new List<AIContent>(parts.Length);
        var inserted = false;
        foreach (var part in parts)
        {
            if (part is TextContent)
            {
                if (!inserted)
                {
                    rebuilt.Add(new TextContent(truncatedText));
                    inserted = true;
                }
            }
            else
            {
                rebuilt.Add(part);
            }
        }

        return rebuilt.ToArray();
    }
}
