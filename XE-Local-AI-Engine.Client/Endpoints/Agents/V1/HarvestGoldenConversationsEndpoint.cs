namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Scans one agent's most-recent thumbs-up assistant turns and stages harvested golden candidates inert
///     (deterministic, no model). Returns the per-run counts; 404 when the agent does not exist. A route-only POST (the
///     client posts <c>{}</c> — FastEndpoints 415s a truly empty body). Operator-gated.
/// </summary>
public sealed class HarvestGoldenConversationsEndpoint(IGoldenHarvestService harvestService)
    : Endpoint<HarvestGoldenConversationsRequest, GoldenHarvestResponse>
{
    private readonly IGoldenHarvestService _harvestService = harvestService ?? throw new ArgumentNullException(nameof(harvestService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.GoldenConversationsHarvest);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(HarvestGoldenConversationsRequest req, CancellationToken ct)
    {
        var outcome = await _harvestService.HarvestAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (!outcome.AgentExists)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(outcome.ToResponse(), ct).ConfigureAwait(false);
    }
}
