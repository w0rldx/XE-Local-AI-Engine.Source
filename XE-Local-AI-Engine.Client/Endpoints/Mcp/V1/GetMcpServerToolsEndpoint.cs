namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     The live connection state plus discovered tools for one registered MCP server.
/// </summary>
/// <remarks>
///     The status is derived from the registration's enabled flag and the connection manager's last refresh:
///     "disabled" when the server is not enabled, so no connection is attempted; "connected" when the last refresh
///     connected it and listed its tools; "connecting" when it is enabled with no recorded failure yet (a refresh has
///     not reached it, or is still in flight); and "error" only for an actually recorded failure — a status entry
///     exists, the server is not connected, and a redacted reason was captured.
/// </remarks>
public sealed class GetMcpServerToolsEndpoint : Endpoint<GetMcpServerToolsRequest, McpServerToolsResponse>
{
    private const string StatusConnected = "connected";
    private const string StatusDisabled = "disabled";
    private const string StatusConnecting = "connecting";
    private const string StatusError = "error";

    private readonly IMcpServerService _mcpServerService;

    public GetMcpServerToolsEndpoint(IMcpServerService mcpServerService)
    {
        ArgumentNullException.ThrowIfNull(mcpServerService);
        _mcpServerService = mcpServerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ServerTools);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetMcpServerToolsRequest req, CancellationToken ct)
    {
        var record = await _mcpServerService.GetByIdAsync(req.McpServerId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A disabled server is never connected, so report disabled regardless of any stale status entry.
        if (!record.Enabled)
        {
            await Send.OkAsync(new McpServerToolsResponse
                {
                    Status = StatusDisabled,
                    Error = null,
                    Tools = []
                },
                ct);
            return;
        }

        var status = _mcpServerService.GetConnectionStatuses()
                                      .FirstOrDefault(entry => entry.ServerId == record.Id);

        if (status is { Connected: true })
        {
            await Send.OkAsync(new McpServerToolsResponse
                {
                    Status = StatusConnected,
                    Error = null,
                    Tools = ProjectDiscoveredTools(status)
                },
                ct);
            return;
        }

        // Enabled but not connected. Only an actually recorded failure — a status entry exists, the server is not connected, and the connection manager captured a redacted
        // reason — is a hard "error"; otherwise the server is still "connecting", which keeps a healthy not-yet-connected server from showing as a failure in the UI.
        if (status is { LastError: { Length: > 0 } recordedError })
        {
            await Send.OkAsync(new McpServerToolsResponse
                {
                    Status = StatusError,
                    Error = recordedError,
                    Tools = []
                },
                ct);
            return;
        }

        await Send.OkAsync(new McpServerToolsResponse
            {
                Status = StatusConnecting,
                Error = null,
                Tools = []
            },
            ct);
    }

    // The connection manager owns the per-server discovered tools, listing them on the status. The qualified name mcp__{serverSlug}__{tool} is the authoritative offered and
    // executable name; the React panel may strip the prefix for display.
    private static IReadOnlyList<McpDiscoveredToolResponse> ProjectDiscoveredTools(McpServerConnectionStatus status)
    {
        return
        [
            .. status.Tools.Select(static tool => new McpDiscoveredToolResponse
            {
                Name = tool.Name,
                Description = tool.Description,
                RequiresApproval = tool.RequiresApproval
            })
        ];
    }
}
