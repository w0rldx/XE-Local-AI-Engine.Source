namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>
///     Read-only cohort monitoring for an agent's Enabled playbook actions (relevance retrieval and cohort monitoring). Computes the before/after
///     down-vote signal on read from the node-local message feedback — no writes, so no mutation guard. The monitor
///     service does not 404, so this endpoint resolves the agent itself and returns 404 when it does not exist. The
///     <c>retrieval</c> block carries the current relevance-gating thresholds for the panel banner. Operator-gated.
/// </summary>
public sealed class GetAgentPlaybookMonitorEndpoint(
    IAgentDefinitionStore agentDefinitionStore,
    IPlaybookMonitorService playbookMonitorService,
    IOptions<PlaybookRetrievalOptions> retrievalOptions)
    : Endpoint<GetAgentPlaybookMonitorRequest, AgentPlaybookMonitorResponse>
{
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IPlaybookMonitorService _playbookMonitorService = playbookMonitorService ?? throw new ArgumentNullException(nameof(playbookMonitorService));
    private readonly PlaybookRetrievalOptions _retrievalOptions = (retrievalOptions ?? throw new ArgumentNullException(nameof(retrievalOptions))).Value;

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.PlaybookMonitor);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAgentPlaybookMonitorRequest req, CancellationToken ct)
    {
        var agent = await _agentDefinitionStore.GetByIdAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var views = await _playbookMonitorService.GetMonitorAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        await Send.OkAsync(views.ToResponse(_retrievalOptions), ct).ConfigureAwait(false);
    }
}
