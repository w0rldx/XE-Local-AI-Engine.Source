namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Node-local MCP (Model Context Protocol) options, bound from the <c>Mcp</c> configuration section.
/// </summary>
/// <remarks>
///     The connection manager uses <see cref="ConnectTimeoutSeconds" /> as a per-server connect and list-tools
///     deadline, so a hung or malicious server cannot stall a refresh, and <see cref="HttpLoopbackHosts" /> as the
///     allow-list a registered HTTP/SSE server URL must resolve to: loopback only by default, because pointing a node
///     at a remote MCP server is a different threat and out of scope.
/// </remarks>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    ///     Per-server timeout in seconds for the connect and initial list-tools handshake during a refresh, which must
    ///     be greater than zero.
    /// </summary>
    /// <remarks>
    ///     A server that does not finish within this window contributes zero tools and is recorded with an error,
    ///     without aborting the other servers or the refresh.
    /// </remarks>
    public int ConnectTimeoutSeconds { get; init; } = 30;

    /// <summary>
    ///     Per-call timeout in seconds for a single model-invoked MCP tool call, which must be greater than zero.
    /// </summary>
    /// <remarks>
    ///     It is distinct from <see cref="ConnectTimeoutSeconds" />, which bounds only the one-shot connect and
    ///     list-tools handshake: without it a wedged server's tool call is bounded only emergently by the 60 s stream
    ///     watchdog and 300 s invocation timeout, stalling the whole turn. On expiry the call returns a typed
    ///     tool-failure result, so the model sees a clean error and the run continues — never a retry, since a tool
    ///     call is non-idempotent.
    /// </remarks>
    public int ToolCallTimeoutSeconds { get; init; } = 60;

    /// <summary>
    ///     Hostnames an HTTP/SSE MCP server URL is permitted to target, matched case-insensitively against the URL
    ///     host.
    /// </summary>
    /// <remarks>
    ///     It defaults to the loopback set. An operator can widen it through configuration, but the default keeps a
    ///     node from being pointed at an arbitrary remote server.
    /// </remarks>
    public IReadOnlyList<string> HttpLoopbackHosts { get; init; } = ["127.0.0.1", "localhost", "::1"];
}
