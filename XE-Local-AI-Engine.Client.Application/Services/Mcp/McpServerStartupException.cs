namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Thrown by an MCP client transport when the server process exited before the MCP handshake completed.
/// </summary>
/// <remarks>
///     <see cref="StderrTail" /> is the last lines the process wrote to standard error, already scrubbed of secrets
///     by the transport, so the connection manager can show the operator why the server did not start.
/// </remarks>
public sealed class McpServerStartupException : Exception
{
    public McpServerStartupException(string message, string? stderrTail) : base(message)
    {
        StderrTail = stderrTail;
    }

    public string? StderrTail { get; }
}
