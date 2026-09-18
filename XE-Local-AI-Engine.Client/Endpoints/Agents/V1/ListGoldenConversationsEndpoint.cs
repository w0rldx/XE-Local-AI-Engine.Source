namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Lists one agent's golden conversation set. Mirrors the playbook-list convention — returns <c>{ items: [...] }</c>
///     (empty for an unknown agent, never a 404). Operator-gated.
/// </summary>
public sealed class ListGoldenConversationsEndpoint : Endpoint<ListGoldenConversationsRequest, ListGoldenConversationsResponse>
{
    private readonly IGoldenConversationService _goldenConversationService;

    public ListGoldenConversationsEndpoint(IGoldenConversationService goldenConversationService)
    {
        ArgumentNullException.ThrowIfNull(goldenConversationService);
        _goldenConversationService = goldenConversationService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.GoldenConversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListGoldenConversationsRequest req, CancellationToken ct)
    {
        var records = await _goldenConversationService.ListByAgentAsync(req.AgentDefinitionId, ct);
        await Send.OkAsync(new ListGoldenConversationsResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct);
    }
}
