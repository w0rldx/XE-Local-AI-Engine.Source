namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Exposes the node's full dynamic tool catalog — built-in tools plus every enabled MCP tool — as the single source
///     the React tool pickers (chat tools overview + the agent-definition tool selector) consume, replacing the former
///     static front-end constant. Each entry carries its source ("builtin" or "mcp:{serverSlug}") so the UI can
///     group/badge tools by their originating server. Model-capability gating is intentionally not applied here: this is
///     the catalog of everything that exists on the node (the offer provider applies gating per active model elsewhere).
/// </summary>
public sealed class GetToolCatalogEndpoint(ILocalToolOfferProvider localToolOfferProvider, IToolApprovalPolicy approvalPolicy)
    : EndpointWithoutRequest<ToolCatalogResponse>
{
    private readonly ILocalToolOfferProvider _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
    private readonly IToolApprovalPolicy _approvalPolicy = approvalPolicy ?? throw new ArgumentNullException(nameof(approvalPolicy));

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ToolCatalog);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var catalog = _localToolOfferProvider.GetKnownTools();
        await Send.OkAsync(new ToolCatalogResponse
            {
                Tools = [.. catalog.Select(entry => entry.ToResponse(_approvalPolicy))]
            },
            ct).ConfigureAwait(false);
    }
}
