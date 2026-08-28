namespace XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;

/// <summary>
///     Pins every outbound request of one external connection to that connection's NORMALIZED base address, and
///     refuses anything else.
/// </summary>
/// <remarks>
///     <para>
///         WHY: the base URL is the only thing the operator reviewed when they declared the connection Local or Cloud,
///         and that declaration is what unlocks workspace tools, the knowledge base and <c>run_python</c> for models on
///         it. Anything that could move the actual destination away from the reviewed address — an SDK path quirk, a
///         <c>301</c>/<c>302</c> to another host, a future call site that builds its own URI — would let a
///         declared-Local connection silently exfiltrate to somewhere the operator never saw. This handler makes that
///         structurally impossible rather than relying on every caller being careful.
///     </para>
///     <para>
///         Redirects are refused rather than followed: auto-redirect is off on the inner handler, so a 3xx surfaces as
///         a response the SDK reports instead of a transparent hop. Following one would defeat the pin, and no
///         OpenAI-compatible chat API legitimately needs a redirect to serve <c>/chat/completions</c>.
///     </para>
/// </remarks>
internal sealed class ExternalEndpointGuardHandler : DelegatingHandler
{
    // Sanitized refusal message: it names neither the configured base address nor the attempted target, because the
    // exception text reaches the chat transcript.
    private const string RefusalMessage = "The request target is outside the configured external connection endpoint.";

    private readonly Uri _baseAddress;

    /// <param name="baseAddress">The connection's normalized, <c>/v1/</c>-terminated base address.</param>
    /// <param name="innerHandler">The transport that performs the request.</param>
    public ExternalEndpointGuardHandler(Uri baseAddress, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The pinned external endpoint must be an absolute URI.", nameof(baseAddress));
        }

        _baseAddress = baseAddress;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsWithinPinnedEndpoint(request.RequestUri))
        {
            throw new HttpRequestException(RefusalMessage);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    ///     True when <paramref name="target" /> is the pinned base address or a path beneath it. Scheme, host and port
    ///     must all match exactly (an https base never admits an http target, and a port change is a different service),
    ///     and the path must be a SEGMENT-wise descendant — the base always ends in <c>/</c>, so a sibling prefix such
    ///     as <c>/v1x/…</c> cannot pass as a child of <c>/v1/</c>.
    /// </summary>
    internal bool IsWithinPinnedEndpoint(Uri? target)
    {
        return target is not null
               && target.IsAbsoluteUri
               && string.Equals(target.Scheme, _baseAddress.Scheme, StringComparison.Ordinal)
               && string.Equals(target.Host, _baseAddress.Host, StringComparison.OrdinalIgnoreCase)
               && target.Port == _baseAddress.Port
               && string.IsNullOrEmpty(target.UserInfo)
               && target.AbsolutePath.StartsWith(_baseAddress.AbsolutePath, StringComparison.Ordinal);
    }
}
