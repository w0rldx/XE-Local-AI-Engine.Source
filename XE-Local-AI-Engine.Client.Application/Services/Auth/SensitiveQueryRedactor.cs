namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Redacts the values of the query parameters that must never reach a log: <c>access_token</c> (the SignalR JWT)
///     and <c>query</c> (the conversation search text, plaintext of encrypted chats).
/// </summary>
public static class SensitiveQueryRedactor
{
    private const string RedactedValue = "[REDACTED]";

    private static readonly string[] SensitiveParameters = ["access_token", "query"];

    public static string Redact(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        var hasQuestionPrefix = queryString[0] == '?';
        var query = hasQuestionPrefix ? queryString[1..] : queryString;
        if (query.Length == 0)
        {
            return hasQuestionPrefix ? "?" : string.Empty;
        }

        var segments = query.Split('&');
        var redacted = false;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var separatorIndex = segment.IndexOf(value: '=', StringComparison.Ordinal);
            var key = separatorIndex >= 0 ? segment[..separatorIndex] : segment;

            // Compared decoded: the server binds a percent-encoded key ("%71uery") as the parameter it spells.
            if (!SensitiveParameters.Contains(Uri.UnescapeDataString(key), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            segments[i] = $"{key}={RedactedValue}";
            redacted = true;
        }

        if (!redacted)
        {
            return queryString;
        }

        var redactedQuery = string.Join(separator: '&', segments);
        return hasQuestionPrefix ? $"?{redactedQuery}" : redactedQuery;
    }
}
