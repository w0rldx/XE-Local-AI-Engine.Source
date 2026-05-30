namespace XE_Local_AI_Engine.Client.Services.Mcp;

using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Creates a connected <see cref="McpClient" /> for a registration. The real implementation builds the transport
///     from the record's transport kind (validating that an HTTP URL targets a loopback host), then calls
///     <c>McpClient.CreateAsync</c>. Abstracted so the connection manager's reconcile/qualify/sort/snapshot logic can be
///     exercised against an in-process fake server without spawning a real stdio process or opening a socket.
/// </summary>
internal interface IMcpClientFactory
{
    Task<McpClient> CreateAsync(McpServerRecord record, CancellationToken cancellationToken);
}
