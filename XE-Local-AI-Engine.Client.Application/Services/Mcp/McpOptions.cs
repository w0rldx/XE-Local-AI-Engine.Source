namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Node-local MCP (Model Context Protocol) options. Bound from the <c>Mcp</c> configuration section. The connection
///     manager uses <see cref="ConnectTimeoutSeconds" /> as a per-server connect/list-tools deadline so a hung or
///     malicious server cannot stall a refresh, and <see cref="HttpLoopbackHosts" /> as the allow-list a registered
///     HTTP/SSE server URL must resolve to (loopback only by default — pointing a node at a remote MCP server is a
///     different threat and is out of scope).
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    ///     Per-server timeout (seconds) for the connect + initial list-tools handshake during a refresh. Must be
    ///     greater than zero. A server that does not finish within this window contributes zero tools and is recorded
    ///     with an error, without aborting the other servers or the refresh.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 30;

    /// <summary>
    ///     Per-call timeout (seconds) for a single model-invoked MCP tool call. Distinct from
    ///     <see cref="ConnectTimeoutSeconds" /> (which bounds only the one-shot connect/list-tools handshake): without
    ///     this a slow or wedged server's tool call is bounded only emergently by the 60 s stream watchdog / 300 s
    ///     invocation timeout, stalling the whole turn. On expiry the call returns a typed tool-failure result so the
    ///     model sees a clean error and the run continues (never a retry — a tool call is non-idempotent). Must be greater
    ///     than zero.
    /// </summary>
    public int ToolCallTimeoutSeconds { get; init; } = 60;

    /// <summary>
    ///     Hostnames an HTTP/SSE MCP server URL is permitted to target. Defaults to the loopback set; an operator can
    ///     widen it via configuration, but the default keeps a node from being pointed at an arbitrary remote server.
    ///     Matched case-insensitively against the URL host.
    /// </summary>
    public IReadOnlyList<string> HttpLoopbackHosts { get; init; } = ["127.0.0.1", "localhost", "::1"];
}
