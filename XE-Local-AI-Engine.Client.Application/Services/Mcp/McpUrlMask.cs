namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Masks the parts of an HTTP MCP server URL that can carry a credential (userinfo and every query value) on the
///     way out of the node, and recognises the masked form an update sends back.
/// </summary>
/// <remarks>
///     New registrations cannot carry userinfo, but rows written before headers existed may hold <c>?token=...</c>,
///     so the URL is masked rather than rejected: a form round-tripping what it was shown must still be able to save.
/// </remarks>
public static class McpUrlMask
{
    public const string Placeholder = "***";

    /// <summary>Returns <paramref name="url" /> with its userinfo and query values replaced by <see cref="Placeholder" />.</summary>
    public static string? Mask(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var masked = url;
        if (uri.UserInfo.Length > 0)
        {
            var userInfoStart = masked.IndexOf("://", StringComparison.Ordinal) + 3;
            var userInfoEnd = masked.IndexOf('@', userInfoStart);
            masked = string.Concat(masked.AsSpan(0, userInfoStart), Placeholder, masked.AsSpan(userInfoEnd));
        }

        var queryStart = masked.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return masked;
        }

        var fragmentStart = masked.IndexOf('#', queryStart);
        var queryEnd = fragmentStart < 0 ? masked.Length : fragmentStart;
        var parameters = masked[(queryStart + 1)..queryEnd]
                         .Split('&')
                         .Select(static parameter =>
                         {
                             var equals = parameter.IndexOf('=', StringComparison.Ordinal);
                             return equals < 0 ? Placeholder : string.Concat(parameter.AsSpan(0, equals + 1), Placeholder);
                         });

        return string.Concat(masked.AsSpan(0, queryStart + 1), string.Join('&', parameters), masked.AsSpan(queryEnd));
    }

    /// <summary>
    ///     The URL to store for an update: the stored URL when <paramref name="incoming" /> is exactly its masked form,
    ///     otherwise what the caller sent with each masked query value restored, by key, from the stored URL.
    /// </summary>
    /// <remarks>
    ///     Restoring by key is what lets an operator edit the port or path of a masked URL without storing the literal
    ///     <see cref="Placeholder" /> as the token. A masked value the stored URL has no key for is refused.
    /// </remarks>
    /// <exception cref="McpServerValidationException">A masked query value has no stored value to restore.</exception>
    public static string? Restore(string? incoming, string? stored)
    {
        if (incoming is null || stored is null || !incoming.Contains(Placeholder, StringComparison.Ordinal))
        {
            return incoming;
        }

        if (string.Equals(incoming, Mask(stored), StringComparison.Ordinal))
        {
            return stored;
        }

        var queryStart = incoming.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return incoming;
        }

        var storedValues = QueryValues(stored);
        var fragmentStart = incoming.IndexOf('#', queryStart);
        var queryEnd = fragmentStart < 0 ? incoming.Length : fragmentStart;
        var parameters = incoming[(queryStart + 1)..queryEnd]
                         .Split('&')
                         .Select(parameter =>
                         {
                             var equals = parameter.IndexOf('=', StringComparison.Ordinal);
                             if (!string.Equals(equals < 0 ? parameter : parameter[(equals + 1)..], Placeholder, StringComparison.Ordinal))
                             {
                                 return parameter;
                             }

                             // Occurrences restore in order, so "?scope=***&scope=***" gets read then write back, never read twice.
                             return equals >= 0 && storedValues.TryGetValue(parameter[..equals], out var values) && values.Count > 0
                                 ? string.Concat(parameter.AsSpan(0, equals + 1), values.Dequeue())
                                 : throw new McpServerValidationException("The URL carries a masked query value with no stored value to restore; enter its value.");
                         });

        return string.Concat(incoming.AsSpan(0, queryStart + 1), string.Join('&', parameters), incoming.AsSpan(queryEnd));
    }

    // Every raw value of each query key, in stored order (a key may repeat).
    private static Dictionary<string, Queue<string>> QueryValues(string url)
    {
        var values = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return values;
        }

        var fragmentStart = url.IndexOf('#', queryStart);
        foreach (var parameter in url[(queryStart + 1)..(fragmentStart < 0 ? url.Length : fragmentStart)].Split('&'))
        {
            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                if (!values.TryGetValue(parameter[..equals], out var queue))
                {
                    queue = new Queue<string>();
                    values[parameter[..equals]] = queue;
                }

                queue.Enqueue(parameter[(equals + 1)..]);
            }
        }

        return values;
    }
}
