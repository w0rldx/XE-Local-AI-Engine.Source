namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Appends a fixed, already-resolved set of operator-defined headers to every request that passes through it.
/// </summary>
/// <remarks>
///     One handler for every request an external connection makes (chat, health and the connect-time probe), so the
///     three cannot drift apart. A reserved name is skipped defensively even though the save rejects it, and a header
///     the request already carries is replaced rather than duplicated. No I/O and no logging: values may be secrets.
/// </remarks>
public sealed class CustomRequestHeadersHandler : DelegatingHandler
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;

    /// <param name="headers">The headers to set, as name and value pairs.</param>
    /// <param name="innerHandler">The next handler in the chain.</param>
    public CustomRequestHeadersHandler(IReadOnlyList<KeyValuePair<string, string>> headers, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(headers);

        _headers = headers;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var (name, value) in _headers)
        {
            if (!CustomHeaderRules.IsValidHeaderName(name) || CustomHeaderRules.IsReservedName(name))
            {
                continue;
            }

            try
            {
                _ = request.Headers.Remove(name);
                _ = request.Headers.TryAddWithoutValidation(name, value);
            }
            catch (InvalidOperationException)
            {
                // A content header .NET refuses on a request ("Misused header name") that the reserved set missed:
                // skipping one header must never fail every request on the connection.
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
