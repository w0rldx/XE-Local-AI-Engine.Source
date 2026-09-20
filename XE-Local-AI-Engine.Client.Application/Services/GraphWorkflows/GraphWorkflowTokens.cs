namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>The two token rules the parser and the condition reader both hold authored text to.</summary>
/// <remarks>
///     One home rather than two: a second spelling of "is this a dot path" is a second answer, and the pair that
///     disagrees is the pair that lets a graph save and then fail to route.
/// </remarks>
internal static class GraphWorkflowTokens
{
    /// <summary>A named enum member, case-insensitively, and NOTHING else.</summary>
    /// <remarks>
    ///     <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)" /> accepts a numeric token — <c>"3"</c>,
    ///     <c>"-1"</c> — and hands back a value no member of the enum has, which then reaches a lookup keyed by kind
    ///     as a missing key or a routing decision nobody wrote.
    /// </remarks>
    public static bool TryParseName<TEnum>(string? raw, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;
        return raw is not null
               && Array.Exists(Enum.GetNames<TEnum>(), name => string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
               && Enum.TryParse(raw, ignoreCase: true, out parsed);
    }

    /// <summary>
    ///     Whether <paramref name="path" /> is a dot path: property names separated by <c>.</c>, each non-empty, with
    ///     no whitespace and none of <c>[ ] * ( )</c>.
    /// </summary>
    /// <remarks>
    ///     Dot paths only — no wildcards, no indexes, no functions. Refusing the rest here is what keeps
    ///     <c>items[0].name</c> from being saved as a property literally called <c>items[0]</c> that no output
    ///     document will ever carry.
    /// </remarks>
    public static bool IsDotPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var segment in path.Split('.'))
        {
            if (segment.Length == 0 || segment.Any(IsNotPathCharacter))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNotPathCharacter(char value) =>
        char.IsWhiteSpace(value) || value is '[' or ']' or '*' or '(' or ')';
}
