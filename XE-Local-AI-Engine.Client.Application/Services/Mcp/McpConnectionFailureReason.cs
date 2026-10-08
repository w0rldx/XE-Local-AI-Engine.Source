namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Why the last connect or list attempt for an MCP server failed, or why a connected server was lost, derived from
///     the exception or the client completion that reached the connection manager.
/// </summary>
public enum McpConnectionFailureReason
{
    /// <summary>A failure no narrower reason covers, such as a registration the connect path rejects.</summary>
    Unknown = 0,

    /// <summary>The server's command was not found or could not be started.</summary>
    ServerNotFound = 1,

    /// <summary>This node cannot isolate a process from the host filesystem, so no Sandboxed server can start.</summary>
    SandboxUnavailable = 2,

    /// <summary>The sandbox refused this server's registration, or could not establish its boundary or launch it.</summary>
    SandboxRefused = 3,

    /// <summary>The per-server connect and list timeout elapsed.</summary>
    Timeout = 4,

    /// <summary>The process, pipe or HTTP connection failed or closed before the tools were listed.</summary>
    Transport = 5,

    /// <summary>The server answered, but not with a valid MCP handshake or tool list.</summary>
    Protocol = 6,

    /// <summary>The server rejected the configured credentials (HTTP 401 with headers configured).</summary>
    Authentication = 7,

    /// <summary>The server demands a credential and the registration has none configured (HTTP 401, no headers).</summary>
    AuthenticationRequired = 8,

    /// <summary>The server refused access for the credentials it was given (HTTP 403).</summary>
    Forbidden = 9,

    /// <summary>TLS negotiation with an https server failed.</summary>
    Tls = 10,

    /// <summary>A connected server's process or stream ended after the handshake.</summary>
    ServerExited = 11,

    /// <summary>An HTTP server no longer recognises the session (HTTP 404), typically because it restarted.</summary>
    SessionLost = 12,

    /// <summary>The server's process exited during startup, before the MCP handshake completed.</summary>
    ServerStartupFailed = 13,

    /// <summary>
    ///     The node's <c>high</c> sandbox security profile requires a containment axis (resource ceilings) this node's sandbox cannot
    ///     serve, so the Sandboxed server is refused rather than started without it (ADR 0020).
    /// </summary>
    SandboxRefusedByProfile = 14
}
