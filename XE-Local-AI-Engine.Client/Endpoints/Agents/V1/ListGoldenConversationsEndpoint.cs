namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Lists one agent's golden conversation set. Mirrors the playbook-list convention — returns <c>{ items: [...] }</c>
///     (empty for an unknown agent, never a 404). Operator-gated.
/// </summary>
public sealed class ListGoldenConversationsEndpoint(IGoldenConversationService goldenConversationService)
    : Endpoint<ListGoldenConversationsRequest, ListGoldenConversationsResponse>
{
    private readonly IGoldenConversationService _goldenConversationService = goldenConversationService ?? throw new ArgumentNullException(nameof(goldenConversationService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.GoldenConversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListGoldenConversationsRequest req, CancellationToken ct)
    {
        var records = await _goldenConversationService.ListByAgentAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListGoldenConversationsResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
