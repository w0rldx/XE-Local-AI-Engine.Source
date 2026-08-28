namespace XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The READ contract over the operator's external OpenAI-compatible connections and their registered models.
/// </summary>
/// <remarks>
///     <para>
///         It lives in the seam layer, not the application layer, because the external provider consumes it and a
///         provider project may reference only <c>Providers.Abstractions</c> (frozen by the layer-dependency tests).
///         The encrypted store behind it lives in the application layer, which is free to reference both.
///     </para>
///     <para>
///         Everything here is a read: the registry never mutates connections, and it never hands out the API key
///         alongside the descriptors. A consumer that only renders, gates or routes takes
///         <see cref="ListRegistrationsAsync" /> / <see cref="TryResolveAsync" />; only the transport calls
///         <see cref="TryResolveTransportBindingAsync" />, which is the ONLY way a key leaves the registry — and it
///         hands the key out bound to the endpoint it belongs to, never on its own.
///     </para>
///     <para>
///         Implementations must be safe to call from a singleton on the chat path and must reflect an operator's save
///         without a process restart — the provider resolves a connection per cold chat client, so a stale cache here
///         would keep sending to a base URL the operator has already changed.
///     </para>
/// </remarks>
public interface IExternalProviderRegistry
{
    /// <summary>
    ///     Every registered model across every connection, in a stable order. Returns an empty list — never
    ///     <see langword="null" /> — when no connection is configured, which is the shipped default.
    /// </summary>
    Task<IReadOnlyList<ExternalProviderModelRegistration>> ListRegistrationsAsync(CancellationToken ct);

    /// <summary>
    ///     Resolves one namespaced <c>ext:{connectionId}/{wireId}</c> id, or <see langword="null" /> when the id is
    ///     malformed, its connection is gone, or the model is no longer registered on it. A <see langword="null" />
    ///     result is the fail-closed signal: callers must treat it as not-routable and cloud-locality, never as a
    ///     benign miss.
    /// </summary>
    Task<ExternalProviderModelRegistration?> TryResolveAsync(string modelId, CancellationToken ct);

    /// <summary>
    ///     The same resolution as <see cref="TryResolveAsync" />, carrying the generation it was read at so the caller
    ///     can VERIFY later that the configuration has not moved underneath it. Key-free, for callers that pin or gate
    ///     rather than authenticate.
    /// </summary>
    Task<ExternalProviderBinding?> TryResolveBindingAsync(string modelId, CancellationToken ct);

    /// <summary>
    ///     The transport's atomic view of one model: endpoint, declared trust, generation and credential, all read out
    ///     of ONE registry generation, or <see langword="null" /> when the id does not resolve.
    /// </summary>
    /// <remarks>
    ///     A keyless connection yields a <see langword="null" /> <see cref="ExternalProviderTransportBinding.ApiKey" />,
    ///     which is a first-class case, not a fallback: a local llama-server rejects nothing, and sending a bogus bearer
    ///     token to an endpoint that does check would fail the request outright — so it means "send NO Authorization
    ///     header", never "send an empty one".
    /// </remarks>
    Task<ExternalProviderTransportBinding?> TryResolveTransportBindingAsync(string modelId, CancellationToken ct);
}
