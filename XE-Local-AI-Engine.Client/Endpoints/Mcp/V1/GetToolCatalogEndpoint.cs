namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The node's full dynamic tool catalog — built-in tools plus every enabled MCP tool — as the single source the
///     React tool pickers consume: the chat tools overview and the agent-definition tool selector.
/// </summary>
/// <remarks>
///     Each entry carries its source ("builtin" or "mcp:{serverSlug}"), so the UI can group and badge tools by their
///     originating server. Model-capability gating is intentionally NOT applied here: this is the catalog of
///     everything that exists on the node, and the offer provider applies gating per active model elsewhere.
/// </remarks>
public sealed class GetToolCatalogEndpoint : EndpointWithoutRequest<ToolCatalogResponse>
{
    private readonly ToolCatalogService _toolCatalog;

    public GetToolCatalogEndpoint(ToolCatalogService toolCatalog)
    {
        ArgumentNullException.ThrowIfNull(toolCatalog);
        _toolCatalog = toolCatalog;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ToolCatalog);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var catalog = await _toolCatalog.GetKnownToolsAsync(ct);
        await Send.OkAsync(new ToolCatalogResponse
            {
                Tools = [.. catalog.Select(entry => entry.ToResponse(_toolCatalog))]
            },
            ct);
    }
}
