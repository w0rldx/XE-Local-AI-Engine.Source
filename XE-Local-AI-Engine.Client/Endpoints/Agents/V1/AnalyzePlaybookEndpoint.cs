namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Analysis staging: runs the analysis agent over one agent's feedback aggregate and persists the resulting
///     Suggested actions for review. Operator-gated.
/// </summary>
/// <remarks>
///     Returns the created suggestions — an empty list when the feedback is below threshold or no proposal survived
///     validation/dedup — and 404 when the agent does not exist.
/// </remarks>
public sealed class AnalyzePlaybookEndpoint : Endpoint<AnalyzePlaybookRequest, ListPlaybookActionsResponse>
{
    private readonly IPlaybookAnalysisService _analysisService;

    public AnalyzePlaybookEndpoint(IPlaybookAnalysisService analysisService)
    {
        ArgumentNullException.ThrowIfNull(analysisService);
        _analysisService = analysisService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookAnalyze);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: no body means no Content-Type, and the default POST "Accepts" metadata (application/json
        // only) would answer 415. Accepting any content-type lets the request through; agentDefinitionId still binds from the route.
        Description(x => x.Accepts<AnalyzePlaybookRequest>());
    }

    public override async Task HandleAsync(AnalyzePlaybookRequest req, CancellationToken ct)
    {
        var outcome = await _analysisService.AnalyzeAsync(req.AgentDefinitionId, ct);
        if (!outcome.AgentExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new ListPlaybookActionsResponse
            {
                Items = [.. outcome.CreatedSuggestions.Select(static record => record.ToResponse())]
            },
            ct);
    }
}
