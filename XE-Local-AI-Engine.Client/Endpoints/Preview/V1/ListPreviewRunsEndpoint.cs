namespace XE_Local_AI_Engine.Client.Endpoints.Preview.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Preview.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     GET <c>preview/runs</c> — every run this node currently knows about (live ones plus terminal ones still inside
///     the replay window). This is what makes a run reachable after its id has left the client's memory: before it
///     existed, a plain page reload orphaned the run with no way to list, reattach to, or cancel it. Operator-gated.
/// </summary>
public sealed class ListPreviewRunsEndpoint(IPreviewWorkflowExecutionService executionService)
    : EndpointWithoutRequest<ListPreviewRunsResponse>
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Preview.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        return Send.OkAsync(new ListPreviewRunsResponse
            {
                Items = [.. _executionService.ListRuns().Select(static run => run.ToResponse())]
            },
            ct);
    }
}
