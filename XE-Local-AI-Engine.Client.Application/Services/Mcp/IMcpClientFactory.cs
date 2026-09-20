namespace XE_Local_AI_Engine.Client.Services.Mcp;

using ModelContextProtocol.Client;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Creates a connected <see cref="McpClient" /> for a registration.
/// </summary>
/// <remarks>
///     The real implementation builds the transport from the record's transport kind, validating that an HTTP URL
///     targets a loopback host, then calls <c>McpClient.CreateAsync</c>. It is abstracted so the connection manager's
///     reconcile, qualify, sort and snapshot logic can run against an in-process fake server, without spawning a real
///     stdio process or opening a socket.
/// </remarks>
internal interface IMcpClientFactory
{
    Task<McpClient> CreateAsync(McpServerRecord record, CancellationToken cancellationToken);
}
