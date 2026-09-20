namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

/// <summary>
///     Connects the enabled MCP servers once at startup by calling
///     <see cref="IMcpServerConnectionManager.RefreshAsync" /> off the hot path.
/// </summary>
/// <remarks>
///     A connect failure at startup is logged and swallowed, never fatal, since the manager already isolates a single
///     bad server. The manager owns client disposal, so <see cref="StopAsync" /> is a no-op.
/// </remarks>
internal sealed class McpServerStartupConnector : IHostedService
{
    private readonly IMcpServerConnectionManager _connectionManager;
    private readonly ILogger<McpServerStartupConnector> _logger;

    public McpServerStartupConnector(IMcpServerConnectionManager connectionManager,
        ILogger<McpServerStartupConnector> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connectionManager.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to connect.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            // Startup MCP connection is best-effort: a node must start even if no MCP server connects. The manager
            // already isolates per-server failures, so reaching here means a refresh-wide fault — log and continue.
            _logger.LogWarning(ex, "Initial MCP server connection refresh failed at startup; MCP tools will be unavailable until the next refresh.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
