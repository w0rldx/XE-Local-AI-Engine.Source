namespace XE_Local_AI_Engine.Client.Services.Auth;

public static class AccessTokenQueryRedactor
{
    private const string AccessTokenParameter = "access_token";
    private const string RedactedValue = "[REDACTED]";

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
            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            var key = separatorIndex >= 0 ? segment[..separatorIndex] : segment;

            if (!string.Equals(key, AccessTokenParameter, StringComparison.OrdinalIgnoreCase))
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

        var redactedQuery = string.Join('&', segments);
        return hasQuestionPrefix ? $"?{redactedQuery}" : redactedQuery;
    }
}
