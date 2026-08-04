namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Lists the bundled files of one skill — names, descriptions, media types and sizes. Contents are deliberately
///     absent: a resource is up to a megabyte of third-party text and the list only needs to say what exists.
/// </summary>
public sealed class ListSkillResourcesEndpoint(IAgentSkillService agentSkillService)
    : Endpoint<ListSkillResourcesRequest, ListSkillResourcesResponse>
{
    private readonly IAgentSkillService _agentSkillService = agentSkillService ?? throw new ArgumentNullException(nameof(agentSkillService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Skills.Resources);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListSkillResourcesRequest req, CancellationToken ct)
    {
        var record = await _agentSkillService.GetByIdAsync(req.SkillId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new ListSkillResourcesResponse
            {
                Items = [.. (record.Resources ?? []).Select(static resource => resource.ToSummary())]
            },
            ct).ConfigureAwait(false);
    }
}
