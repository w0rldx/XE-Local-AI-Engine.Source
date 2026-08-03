namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

/// <summary>
///     The inbound-MCP credential as returned to the operator. <see cref="Key" /> carries the full secret and is
///     populated ONLY on this response family, which is Operator-gated and loopback-only. It is deliberately reversible
///     so the operator can re-copy the key into a client config without invalidating clients already using it.
/// </summary>
public sealed record McpServerApiKeyResponse
{
    /// <summary>Non-secret display prefix, safe to show in a list or a log line.</summary>
    public required string Prefix { get; init; }

    /// <summary>The full bearer key.</summary>
    public required string Key { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last successful authentication, or null if the key has never been used by a client.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
///     Whether a credential exists, plus the connection details an operator needs to configure a client. Returned by the
///     GET when no key has been generated, so the UI can render the "generate one" state without a 404 round trip.
/// </summary>
public sealed record McpServerApiKeyStatusResponse
{
    public required bool Configured { get; init; }

    public McpServerApiKeyResponse? ApiKey { get; init; }

    /// <summary>The absolute loopback URL an external MCP client should target, derived from the live request.</summary>
    public required string EndpointUrl { get; init; }
}
