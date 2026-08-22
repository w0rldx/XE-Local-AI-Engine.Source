namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>Selects the trust scope for a newly generated inbound-MCP key.</summary>
public sealed record GenerateMcpServerApiKeyRequest
{
    public McpServerApiKeyScope Scope { get; init; } = McpServerApiKeyScope.Delegate;
}

/// <summary>
///     The inbound-MCP credential's non-secret metadata. Carries no key: the node stores only a one-way digest, so
///     there is nothing to reveal on a retrieval path even to an Operator-gated, loopback-only caller.
/// </summary>
public sealed record McpServerApiKeyResponse
{
    /// <summary>Non-secret display prefix, safe to show in a list or a log line.</summary>
    public required string Prefix { get; init; }

    public required McpServerApiKeyScope Scope { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last successful authentication, or null if the key has never been used by a client.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
///     Whether a credential exists, plus the connection details an operator needs to configure a client. Returned by the
///     GET when no key has been generated, so the UI can render the "generate one" state without a 404 round trip.
///     <para>
///         There is deliberately no key field on this type. The plaintext appears only on
///         <see cref="GeneratedMcpServerApiKeyResponse" />, so "the GET cannot leak the key" is enforced by the shape
///         of the contract rather than by a reviewer noticing a comment.
///     </para>
/// </summary>
public sealed record McpServerApiKeyStatusResponse
{
    public required bool Configured { get; init; }

    public McpServerApiKeyResponse? ApiKey { get; init; }

    /// <summary>The absolute loopback URL an external MCP client should target, derived from the live request.</summary>
    public required string EndpointUrl { get; init; }
}

/// <summary>
///     The response to minting a key: the status shape plus the ONE-TIME plaintext <see cref="Key" />. The node keeps
///     only a SHA-256 digest, so this response body is the only place the key will ever exist. An operator who does not
///     copy it now cannot recover it — the only remedy is to generate another and reconfigure every client.
/// </summary>
public sealed record GeneratedMcpServerApiKeyResponse
{
    public required bool Configured { get; init; }

    public McpServerApiKeyResponse? ApiKey { get; init; }

    /// <summary>The absolute loopback URL an external MCP client should target, derived from the live request.</summary>
    public required string EndpointUrl { get; init; }

    /// <summary>The full bearer key, shown exactly once. Never logged, never stored, never returned again.</summary>
    public required string Key { get; init; }
}
