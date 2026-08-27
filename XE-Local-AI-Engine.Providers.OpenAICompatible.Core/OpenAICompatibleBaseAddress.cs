namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Normalizes an operator-entered endpoint into the canonical <c>…/v1</c> base address the OpenAI SDK appends its
///     operation paths to (<c>/chat/completions</c>, <c>/models</c>, …).
/// </summary>
/// <remarks>
///     <para>
///         WHY normalization has to happen exactly ONCE, at save time: the same value feeds the connect-time probe, the
///         chat transport, and the outbound guard that pins every request to it. If the probe normalized one way and
///         the guard another, the guard would either reject legitimate traffic or — worse — admit a base it never
///         verified. Everything downstream therefore consumes the already-normalized value and never re-derives it.
///     </para>
///     <para>
///         Accepted: <c>http(s)://host[:port]</c> and <c>http(s)://host[:port]/some/prefix</c>, with or without a
///         trailing <c>/v1</c> and with or without a trailing slash. Rejected: any non-http(s) scheme (a
///         <c>file://</c> or <c>ws://</c> endpoint is never an OpenAI-compatible API), embedded userinfo
///         (<c>https://user:pass@host</c> — credentials belong in the encrypted key field, not in a base URL that is
///         logged and rendered), a query string, and a fragment. A relative URI is rejected outright: the guard can
///         only pin an absolute origin.
///     </para>
/// </remarks>
public static class OpenAICompatibleBaseAddress
{
    /// <summary>The OpenAI-compatible API version segment every accepted base address ends with.</summary>
    private const string VersionSegment = "v1";

    /// <summary>
    ///     Normalizes <paramref name="baseUrl" /> to a canonical <c>…/v1/</c>-terminated absolute address, or returns
    ///     <see langword="false" /> when it is not an acceptable OpenAI-compatible endpoint.
    /// </summary>
    /// <param name="baseUrl">The operator-entered endpoint.</param>
    /// <param name="normalized">The canonical base address on success.</param>
    public static bool TryNormalize(string? baseUrl, [NotNullWhen(true)] out Uri? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return TryNormalize(parsed, out normalized);
    }

    /// <summary>
    ///     Normalizes an already-parsed absolute <paramref name="baseUrl" />. Returns <see langword="false" /> for the
    ///     rejected shapes documented on this type.
    /// </summary>
    /// <param name="baseUrl">The operator-entered endpoint.</param>
    /// <param name="normalized">The canonical base address on success.</param>
    public static bool TryNormalize(Uri? baseUrl, [NotNullWhen(true)] out Uri? normalized)
    {
        normalized = null;
        if (baseUrl is null
            || !baseUrl.IsAbsoluteUri
            || (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || !string.IsNullOrEmpty(baseUrl.UserInfo)
            || !string.IsNullOrEmpty(baseUrl.Query)
            || !string.IsNullOrEmpty(baseUrl.Fragment)
            || string.IsNullOrEmpty(baseUrl.Host))
        {
            return false;
        }

        // Path segments are rebuilt rather than string-concatenated so "…/v1", "…/v1/", "…/" and "" all converge on
        // one spelling, and so an already-normalized value is a fixed point (re-normalizing never appends a second /v1).
        var segments = baseUrl.AbsolutePath
                              .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .ToList();
        if (segments.Count == 0 || !string.Equals(segments[^1], VersionSegment, StringComparison.Ordinal))
        {
            segments.Add(VersionSegment);
        }

        var builder = new UriBuilder(baseUrl)
        {
            // A trailing slash is load-bearing for Uri-relative resolution: without it the SDK's operation path would
            // replace the last segment ("…/chat/completions" instead of "…/v1/chat/completions").
            Path = string.Concat("/", string.Join('/', segments), "/"),
            Query = string.Empty,
            Fragment = string.Empty
        };

        normalized = builder.Uri;
        return true;
    }

    /// <summary>
    ///     Normalizes <paramref name="baseUrl" /> or throws. For call sites that already hold a value the save path
    ///     validated, where an un-normalizable address is a defect rather than user input.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not an acceptable OpenAI-compatible endpoint.</exception>
    public static Uri Normalize(Uri? baseUrl)
    {
        if (!TryNormalize(baseUrl, out var normalized))
        {
            throw new ArgumentException("An OpenAI-compatible base URL must be an absolute http(s) address without userinfo, query, or fragment.",
                nameof(baseUrl));
        }

        return normalized;
    }
}
