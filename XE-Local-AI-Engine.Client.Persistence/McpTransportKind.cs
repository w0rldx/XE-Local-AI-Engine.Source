namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Transport used to reach a registered MCP server. <see cref="Stdio" /> launches a local process by
///     command/arguments/environment (the canonical local MCP server); <see cref="Http" /> connects to an
///     already-running server by a loopback-only URL. Stored as an int on <c>McpServerRegistration</c>.
/// </summary>
public enum McpTransportKind
{
    Stdio = 0,
    Http = 1
}
