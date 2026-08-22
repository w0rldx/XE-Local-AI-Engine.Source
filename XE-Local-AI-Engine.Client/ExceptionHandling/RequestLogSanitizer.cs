namespace XE_Local_AI_Engine.Client.ExceptionHandling;

using System.Globalization;

/// <summary>
///     Keeps request-derived structured log values on one physical record by rendering control characters as visible
///     Unicode escapes. Ordinary values are returned unchanged.
/// </summary>
internal static class RequestLogSanitizer
{
    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Keep the recognized string-replacement sanitization barrier at this trust boundary. The explicit replacements
        // cover every Unicode line terminator, including the separators that char.IsControl does not classify as controls.
        var sanitized = value.Replace("\r", "\\u000D", StringComparison.Ordinal)
                             .Replace("\n", "\\u000A", StringComparison.Ordinal)
                             .Replace("\u0085", "\\u0085", StringComparison.Ordinal)
                             .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
                             .Replace("\u2029", "\\u2029", StringComparison.Ordinal);

        foreach (var character in value.Distinct()
                                       .Where(static character => char.IsControl(character)
                                                                  && character is not '\r' and not '\n' and not '\u0085'))
        {
            sanitized = sanitized.Replace(character.ToString(),
                $"\\u{((int)character).ToString("X4", CultureInfo.InvariantCulture)}",
                StringComparison.Ordinal);
        }

        return sanitized;
    }
}
