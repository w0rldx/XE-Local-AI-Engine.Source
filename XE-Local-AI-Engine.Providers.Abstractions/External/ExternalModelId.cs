namespace XE_Local_AI_Engine.Providers.Abstractions.External;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     The single source of truth for the namespaced external-model identity <c>ext:{connectionId}/{wireId}</c> — its
///     grammar, its formatting, its parsing and its canonical form.
/// </summary>
/// <remarks>
///     <para>
///         WHY a namespaced id at all: the node routes a chat turn by ONE model-name string through the provider map,
///         and two operators can legitimately register the same backing model id (<c>qwen3-27b</c>) on two different
///         connections. Prefixing with the connection slug makes the identity unique without a second routing key, and
///         the <c>ext:</c> scheme lets every policy site recognise an external id by inspection alone.
///     </para>
///     <para>
///         WHY the grammar is deliberately narrow on the LEFT and wide on the RIGHT: the connection slug is ours to
///         mint, so it is restricted to <c>[a-z0-9-]</c> and canonicalized once at write time — the provider map
///         compares model names case-INSENSITIVELY while the tool-capable allow-list compares them ORDINALLY, and a
///         single canonical spelling is what keeps those two agreeing. The wire id is the REMOTE server's, so it is
///         only constrained enough to stay safe: it may carry <c>/</c> (an <c>org/model</c> vLLM id) and <c>:</c> (an
///         Ollama-style tag), but never a path traversal, a backslash, a scheme, or whitespace.
///     </para>
/// </remarks>
public static class ExternalModelId
{
    /// <summary>The scheme prefix that marks a model id as external, including its separator.</summary>
    public const string Scheme = "ext:";

    /// <summary>Maximum length of the connection slug.</summary>
    public const int MaxConnectionIdLength = 32;

    /// <summary>Maximum length of the backing wire id.</summary>
    public const int MaxWireIdLength = 128;

    /// <summary>
    ///     Maximum length of a whole namespaced id: <c>ext:</c> + the longest slug + <c>/</c> + the longest wire id.
    ///     The model-name validator raises its general bound to exactly this for <c>ext:</c> ids only.
    /// </summary>
    public const int MaxLength = 4 + MaxConnectionIdLength + 1 + MaxWireIdLength;

    /// <summary>The connection-slug grammar, as a human-readable pattern for validation messages and documentation.</summary>
    public const string ConnectionIdPattern = "[a-z0-9-]{1,32}";

    /// <summary>
    ///     Builds the canonical namespaced id. The connection slug is lowered (the canonical spelling); the wire id is
    ///     preserved EXACTLY, because it is what goes on the wire as the request's <c>model</c> field and remote model
    ///     ids are case-sensitive.
    /// </summary>
    /// <exception cref="ArgumentException">Either part violates the grammar.</exception>
    public static string Format(string connectionId, string wireId)
    {
        var canonicalConnectionId = CanonicalizeConnectionId(connectionId);
        if (!IsValidConnectionId(canonicalConnectionId))
        {
            throw new ArgumentException($"An external connection id must match {ConnectionIdPattern}.", nameof(connectionId));
        }

        if (!IsValidWireId(wireId))
        {
            throw new ArgumentException($"An external model wire id must be 1-{MaxWireIdLength} characters of [A-Za-z0-9._:/-] with no traversal or edge slash.",
                nameof(wireId));
        }

        return string.Concat(Scheme, canonicalConnectionId, "/", wireId);
    }

    /// <summary>
    ///     True when <paramref name="modelName" /> carries the external scheme — a cheap, allocation-free inspection
    ///     every policy site can run before paying for a registry lookup. It does NOT assert the rest of the grammar;
    ///     use <see cref="TryParse" /> when the parts are needed.
    /// </summary>
    public static bool HasExternalScheme([NotNullWhen(true)] string? modelName)
    {
        return modelName is not null && modelName.StartsWith(Scheme, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Parses a namespaced id into its canonical parts. Returns <see langword="false" /> — never throws — for any
    ///     input that is not a well-formed external id, so a malformed or hand-edited id resolves to the caller's
    ///     fail-closed branch rather than an exception on the routing path.
    /// </summary>
    /// <param name="modelName">The candidate id.</param>
    /// <param name="connectionId">The canonical (lowered) connection slug on success.</param>
    /// <param name="wireId">The backing model id, exactly as it must appear on the wire, on success.</param>
    public static bool TryParse(string? modelName,
        [NotNullWhen(true)]
        out string? connectionId,
        [NotNullWhen(true)]
        out string? wireId)
    {
        connectionId = null;
        wireId = null;

        if (!HasExternalScheme(modelName) || modelName.Length > MaxLength)
        {
            return false;
        }

        var remainder = modelName.AsSpan(Scheme.Length);
        var separator = remainder.IndexOf('/');
        if (separator <= 0 || separator == remainder.Length - 1)
        {
            return false;
        }

        var candidateConnectionId = CanonicalizeConnectionId(remainder[..separator].ToString());
        var candidateWireId = remainder[(separator + 1)..].ToString();
        if (!IsValidConnectionId(candidateConnectionId) || !IsValidWireId(candidateWireId))
        {
            return false;
        }

        connectionId = candidateConnectionId;
        wireId = candidateWireId;
        return true;
    }

    /// <summary>
    ///     Returns the canonical spelling of <paramref name="modelName" />, or <see langword="null" /> when it is not a
    ///     well-formed external id. Writers canonicalize ONCE with this, so the case-insensitive provider map and the
    ///     ordinal tool-capable allow-list can never disagree about the same model.
    /// </summary>
    public static string? Canonicalize(string? modelName)
    {
        return TryParse(modelName, out var connectionId, out var wireId) ? string.Concat(Scheme, connectionId, "/", wireId) : null;
    }

    /// <summary>True when <paramref name="connectionId" /> is already in canonical slug form.</summary>
    // Hand-validated rather than regex-matched: the grammars are single character classes, so a linear scan is both
    // cheaper and free of the catastrophic-backtracking surface a regex on caller-supplied input carries (MA0009).
    public static bool IsValidConnectionId([NotNullWhen(true)] string? connectionId)
    {
        return connectionId is { Length: > 0 and <= MaxConnectionIdLength }
               && connectionId.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');
    }

    /// <summary>
    ///     True when <paramref name="wireId" /> is a safe backing model id: within the charset and length bound, free of
    ///     path traversal, and without an empty leading/trailing/interior path segment.
    /// </summary>
    public static bool IsValidWireId([NotNullWhen(true)] string? wireId)
    {
        // Charset: letters, digits, and the punctuation real remote model ids use — dot, underscore, dash, colon (tags)
        // and slash (org/model). Everything else, whitespace / backslash / percent / '@' included, is refused.
        return wireId is { Length: > 0 and <= MaxWireIdLength }
               && wireId.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/')
               && !wireId.Contains("..", StringComparison.Ordinal)
               && !wireId.Contains("//", StringComparison.Ordinal)
               && !wireId.StartsWith('/')
               && !wireId.EndsWith('/');
    }

    /// <summary>
    ///     Returns the canonical spelling of a connection slug — trimmed and lowered — WITHOUT asserting the grammar;
    ///     pair it with <see cref="IsValidConnectionId" /> when the input is operator-supplied. Public so the store that
    ///     MINTS slugs canonicalizes them with the same code that parses them back out of a model id: two independent
    ///     lowering passes are how the case-insensitive provider map and the ordinal allow-list drift apart.
    /// </summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "The connection slug's canonical persisted form is lowercase ASCII by grammar; it is an identity segment we mint, not a security token compared after a round-trip.")]
    public static string CanonicalizeConnectionId(string? connectionId)
    {
        return connectionId is null ? string.Empty : connectionId.Trim().ToLowerInvariant();
    }
}
