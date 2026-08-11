namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;

/// <summary>
///     The inbound model-proxy credential's non-secret metadata. Carries no key: the node stores only a one-way digest,
///     so there is nothing to reveal on a retrieval path even to an Operator-gated, loopback-only caller.
/// </summary>
public sealed record LocalModelProxyApiKeyResponse
{
    /// <summary>Non-secret display prefix, safe to show in a list or a log line.</summary>
    public required string Prefix { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last successful authentication, or null if the key has never been used by a client.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
///     Whether a credential exists, plus the connection details an operator needs to configure an external tool. Returned
///     by the GET when no key has been generated, so the UI can render the "generate one" state without a 404 round trip.
///     <para>
///         There is deliberately no key field on this type. The plaintext appears only on
///         <see cref="GeneratedLocalModelProxyApiKeyResponse" />, so "the GET cannot leak the key" is enforced by the
///         shape of the contract rather than by a reviewer noticing a comment.
///     </para>
/// </summary>
public sealed record LocalModelProxyApiKeyStatusResponse
{
    public required bool Configured { get; init; }

    public LocalModelProxyApiKeyResponse? ApiKey { get; init; }

    /// <summary>The absolute loopback base URL an external OpenAI-compatible tool should set as its <c>base_url</c>, derived from the live request.</summary>
    public required string EndpointUrl { get; init; }
}

/// <summary>
///     The response to minting a key: the status shape plus the ONE-TIME plaintext <see cref="Key" />. The node keeps
///     only a SHA-256 digest, so this response body is the only place the key will ever exist. An operator who does not
///     copy it now cannot recover it — the only remedy is to generate another and reconfigure every client.
/// </summary>
public sealed record GeneratedLocalModelProxyApiKeyResponse
{
    public required bool Configured { get; init; }

    public LocalModelProxyApiKeyResponse? ApiKey { get; init; }

    /// <summary>The absolute loopback base URL an external OpenAI-compatible tool should set as its <c>base_url</c>, derived from the live request.</summary>
    public required string EndpointUrl { get; init; }

    /// <summary>The full bearer key, shown exactly once. Never logged, never stored, never returned again.</summary>
    public required string Key { get; init; }
}
