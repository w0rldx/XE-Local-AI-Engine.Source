namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Shared bounds for a knowledge-base search query. The agent tool JSON schema, the agent tool handler, and the HTTP
///     search endpoint all reference these so the advertised limit and the two authoritative validation sites cannot
///     drift apart. <see cref="MaxQueryLength" /> is the stricter of the historical bounds (the endpoint's 1000, versus
///     the tool schema's former advisory 2000).
/// </summary>
public static class KnowledgeQueryLimits
{
    /// <summary>Maximum number of characters accepted in a knowledge-base search query, measured after trimming.</summary>
    public const int MaxQueryLength = 1000;

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="query" /> exceeds <see cref="MaxQueryLength" /> characters
    ///     after trimming surrounding whitespace. Callers guard for a null / whitespace query first; this method only
    ///     evaluates the upper bound.
    /// </summary>
    public static bool ExceedsMaxLength(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Trim().Length > MaxQueryLength;
    }
}
