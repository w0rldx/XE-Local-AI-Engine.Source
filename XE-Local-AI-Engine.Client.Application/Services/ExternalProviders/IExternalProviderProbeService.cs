namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     The connect-time reachability check an operator runs before (or after) saving a connection: one server-side
///     <c>GET {normalized-base}/models</c>, reported as a verdict rather than an exception.
/// </summary>
/// <remarks>
///     It runs on the NODE, never in the browser: an operator endpoint on a LAN address serves no CORS headers, and the
///     node is the only side that can read the stored API key. The probe is advisory — a 404 or an unparseable body is
///     "answered, no listing", never a failure that blocks the save, and only a transport-level failure means the
///     endpoint could not be reached at all. Why, and the three load-bearing properties:
///     docs/wiki/03-local-runtime-and-providers.md, "The connect-time probe".
/// </remarks>
public interface IExternalProviderProbeService
{
    /// <summary>Probes a stored connection or an unsaved draft. Never throws for a remote failure; only cancellation propagates.</summary>
    Task<ExternalProviderProbeResult> ProbeAsync(ExternalProviderProbeQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
///     What to probe: a stored connection (<paramref name="ConnectionId" />), an unsaved draft
///     (<paramref name="BaseUrl" />), or both.
/// </summary>
/// <remarks>
///     Both together is a draft base URL under an existing connection id, which is what the editor sends while the operator
///     retypes the address of a connection whose key they have not re-entered. The masked editor sends no key back, so falling
///     back to the stored one is what makes "Test connection" work on an existing connection without re-typing the secret.
/// </remarks>
/// <param name="ConnectionId">The stored connection to resolve the base URL and, when <paramref name="ApiKey" /> is absent, the API key from.</param>
/// <param name="BaseUrl">A raw, operator-entered endpoint, not yet saved. Takes precedence over the stored address and is normalized here by the save path's normalizer.</param>
/// <param name="ApiKey">An explicitly supplied key. Takes precedence over the stored one; blank means "use the stored key, if any".</param>
public readonly record struct ExternalProviderProbeQuery(string? ConnectionId, string? BaseUrl, string? ApiKey);

/// <summary>Why a probe could not even be attempted, or how the endpoint answered.</summary>
public enum ExternalProviderProbeOutcome
{
    /// <summary>The endpoint answered. <see cref="ExternalProviderProbeResult.Error" /> may still explain a non-2xx status or an unusable body.</summary>
    Answered = 0,

    /// <summary>The endpoint could not be reached at all: DNS, connect, TLS, or the probe's own timeout.</summary>
    Unreachable = 1,

    /// <summary>The request named a connection id that is not stored. Nothing was sent.</summary>
    UnknownConnection = 2,

    /// <summary>The supplied base URL is not an acceptable OpenAI-compatible endpoint. Nothing was sent.</summary>
    InvalidBaseUrl = 3
}

/// <summary>The outcome of one probe.</summary>
public sealed record ExternalProviderProbeResult
{
    /// <summary>How far the probe got.</summary>
    public required ExternalProviderProbeOutcome Outcome { get; init; }

    /// <summary>
    ///     An operator-safe explanation, or <see langword="null" /> when the listing came back cleanly. NEVER carries
    ///     the API key, the raw exception text (which can embed the address and credentials), or a response body.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>The model ids the endpoint listed, in the order it listed them. Empty when it serves no usable listing.</summary>
    public IReadOnlyList<ExternalProviderProbeModel> Models { get; init; } = [];
}

/// <summary>
///     One model id from a <c>/v1/models</c> payload, with the context window when the server volunteered one.
/// </summary>
/// <param name="Id">The backing model id, exactly as the server spells it — this is what goes on the wire.</param>
/// <param name="ContextLength">
///     The declared window from <c>max_model_len</c> (vLLM) or <c>context_length</c>, or <see langword="null" />. Most
///     servers report neither; it pre-fills the registration form and is never assumed.
/// </param>
public readonly record struct ExternalProviderProbeModel(string Id, int? ContextLength);
