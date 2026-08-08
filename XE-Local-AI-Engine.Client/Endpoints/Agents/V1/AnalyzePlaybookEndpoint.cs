namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     analysis staging: runs the analysis agent over one agent's feedback aggregate and persists the resulting Suggested
///     actions for review. Returns the created suggestions (an empty list when the feedback is below threshold or no
///     proposal survived validation/dedup); 404 when the agent does not exist. Operator-gated.
/// </summary>
public sealed class AnalyzePlaybookEndpoint(IPlaybookAnalysisService analysisService)
    : Endpoint<AnalyzePlaybookRequest, ListPlaybookActionsResponse>
{
    private readonly IPlaybookAnalysisService _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookAnalyze);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the agent id comes from the route, so a well-behaved client sends no body — and therefore no
        // Content-Type. The default POST "Accepts" metadata only allows application/json, which FastEndpoints answers
        // with 415 when the header is absent. Overriding Accepts to accept any content-type lets a body-less request
        // through (the agentDefinitionId still binds from the route).
        Description(x => x.Accepts<AnalyzePlaybookRequest>());
    }

    public override async Task HandleAsync(AnalyzePlaybookRequest req, CancellationToken ct)
    {
        var outcome = await _analysisService.AnalyzeAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (!outcome.AgentExists)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new ListPlaybookActionsResponse
            {
                Items = [.. outcome.CreatedSuggestions.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
