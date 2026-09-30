namespace XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Thrown by the connection manager's call router when no live session can serve a tool call. The message is
///     model-facing and safe: it names the server and a redacted reason, never a path, URL or secret.
/// </summary>
public sealed class McpServerUnavailableException : Exception
{
    public McpServerUnavailableException(string message) : base(message)
    {
    }

    /// <summary>The wording for a server that is not connected and could not be reconnected.</summary>
    public static McpServerUnavailableException NotConnected(string serverName, string reason)
    {
        return new McpServerUnavailableException($"MCP server '{serverName}' is not connected: {reason} The next call retries; use Reconnect on the MCP page if it keeps failing.");
    }
}
