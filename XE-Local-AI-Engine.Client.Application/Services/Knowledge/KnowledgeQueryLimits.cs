namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The outcome of validating and normalizing a knowledge-base search query.
/// </summary>
public enum KnowledgeQueryValidation
{
    /// <summary>The query is non-empty, within both bounds, and the normalized form is returned.</summary>
    Valid,

    /// <summary>The query was null or whitespace-only.</summary>
    Empty,

    /// <summary>The query exceeds the raw transport cap or the trimmed content bound.</summary>
    TooLong
}

/// <summary>
///     Shared bounds for a knowledge-base search query. The agent tool JSON schema, the agent tool handler, and the HTTP
///     search endpoint all validate through <see cref="ValidateAndNormalize" /> so the advertised limit and the two
///     authoritative validation sites cannot drift apart, and both forward the SAME normalized (trimmed) query to search.
///     <see cref="MaxQueryLength" /> is the stricter historical content bound (the endpoint's 1000, versus the tool
///     schema's former advisory 2000); <see cref="MaxRawQueryLength" /> is a raw transport cap applied BEFORE trimming so
///     a pathologically whitespace-padded payload cannot slip a huge raw string past the trimmed content check.
/// </summary>
public static class KnowledgeQueryLimits
{
    /// <summary>Maximum number of characters accepted in a knowledge-base search query, measured after trimming.</summary>
    public const int MaxQueryLength = 1000;

    /// <summary>
    ///     Maximum RAW length accepted before trimming, at twice the content bound. This rejects an oversized transport
    ///     payload (e.g. 100k spaces wrapped around a short query) up front, while still allowing reasonable surrounding
    ///     whitespace around a full-length query.
    /// </summary>
    public const int MaxRawQueryLength = MaxQueryLength * 2;

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="query" /> exceeds <see cref="MaxQueryLength" /> characters
    ///     after trimming surrounding whitespace. Callers guard for a null / whitespace query first; this method only
    ///     evaluates the trimmed content bound.
    /// </summary>
    public static bool ExceedsMaxLength(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Trim().Length > MaxQueryLength;
    }

    /// <summary>
    ///     Validates the raw query against both the raw transport cap and the trimmed content bound, and — when
    ///     <see cref="KnowledgeQueryValidation.Valid" /> — returns the normalized (trimmed) query the caller MUST forward
    ///     to search. Rejecting on the raw length first means a whitespace-padded oversized payload never reaches the trim.
    /// </summary>
    public static KnowledgeQueryValidation ValidateAndNormalize(string? rawQuery, out string normalizedQuery)
    {
        normalizedQuery = string.Empty;
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return KnowledgeQueryValidation.Empty;
        }

        if (rawQuery.Length > MaxRawQueryLength)
        {
            return KnowledgeQueryValidation.TooLong;
        }

        var trimmed = rawQuery.Trim();
        if (trimmed.Length > MaxQueryLength)
        {
            return KnowledgeQueryValidation.TooLong;
        }

        normalizedQuery = trimmed;
        return KnowledgeQueryValidation.Valid;
    }
}
