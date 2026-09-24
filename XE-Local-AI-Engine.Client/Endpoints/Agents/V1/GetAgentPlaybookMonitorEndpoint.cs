namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Monitoring;

/// <summary>
///     Read-only cohort monitoring for an agent's Enabled playbook actions (relevance retrieval and cohort
///     monitoring): the before/after down-vote signal computed on read from the node-local message feedback.
/// </summary>
/// <remarks>
///     Operator-gated, and no writes, so no mutation guard. The monitor service does not 404, so this endpoint
///     resolves the agent itself and returns 404 when it does not exist. The <c>retrieval</c> block carries the
///     current relevance-gating thresholds for the panel banner.
/// </remarks>
public sealed class GetAgentPlaybookMonitorEndpoint : Endpoint<GetAgentPlaybookMonitorRequest, AgentPlaybookMonitorResponse>
{
    private readonly IAgentDefinitionService _agentDefinitions;
    private readonly IPlaybookMonitorService _playbookMonitorService;
    private readonly PlaybookRetrievalOptions _retrievalOptions;

    public GetAgentPlaybookMonitorEndpoint(IAgentDefinitionService agentDefinitions,
        IPlaybookMonitorService playbookMonitorService,
        IOptions<PlaybookRetrievalOptions> retrievalOptions)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitions);
        _agentDefinitions = agentDefinitions;
        ArgumentNullException.ThrowIfNull(playbookMonitorService);
        _playbookMonitorService = playbookMonitorService;
        _retrievalOptions = (retrievalOptions ?? throw new ArgumentNullException(nameof(retrievalOptions))).Value;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.PlaybookMonitor);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAgentPlaybookMonitorRequest req, CancellationToken ct)
    {
        var agent = await _agentDefinitions.GetByIdAsync(req.AgentDefinitionId, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var views = await _playbookMonitorService.GetMonitorAsync(req.AgentDefinitionId, ct);
        await Send.OkAsync(views.ToResponse(_retrievalOptions), ct);
    }
}
