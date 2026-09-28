namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Why the last connect or list attempt for an MCP server failed, derived from the exception that reached the
///     connection manager.
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

    /// <summary>The server rejected the connection's credentials or TLS negotiation failed.</summary>
    Authentication = 7
}
