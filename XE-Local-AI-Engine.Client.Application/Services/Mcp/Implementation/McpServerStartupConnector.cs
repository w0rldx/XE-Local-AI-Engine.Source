namespace XE_Local_AI_Engine.Client.Services.Mcp.Implementation;

/// <summary>
///     Connects the enabled MCP servers once at startup by calling
///     <see cref="IMcpServerConnectionManager.RefreshAsync(CancellationToken)" /> off the host start path.
/// </summary>
/// <remarks>
///     Host start never waits on a connect, so a server that accepts but never answers cannot hold the UI down. A
///     failure is logged and swallowed, never fatal: a node must start even if no MCP server connects. The manager
///     owns client disposal.
/// </remarks>
internal sealed class McpServerStartupConnector : BackgroundService
{
    private readonly IMcpServerConnectionManager _connectionManager;
    private readonly ILogger<McpServerStartupConnector> _logger;

    public McpServerStartupConnector(IMcpServerConnectionManager connectionManager,
        ILogger<McpServerStartupConnector> logger)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so host startup is never blocked on a connect.
        await Task.Yield();

        try
        {
            await _connectionManager.RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down before the connect finished; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial MCP server connection refresh failed at startup; MCP tools will be unavailable until the next refresh.");
        }
    }
}
