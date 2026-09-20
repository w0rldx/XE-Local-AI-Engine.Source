namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     What landing a run's exported patch would do to the host, without doing any of it. A patch that cannot apply
///     is still a 200, carrying <c>canApply: false</c> and the reasons — the answer the operator asked for. Only
///     "no patch at all" is a 404.
/// </summary>
public sealed class PreviewAgentHomePatchEndpoint : Endpoint<AgentHomePatchPreviewRequest, AgentHomePatchPreviewResponse>
{
    private readonly INodePatchApplyService _service;

    public PreviewAgentHomePatchEndpoint(INodePatchApplyService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.AgentHomePatch.Preview);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(AgentHomePatchPreviewRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var preview = await _service.PreviewAsync(new NodePatchApplyRequest
            {
                RunId = req.RunId
            },
            ct);

        // Only "there is no patch" is a 404. A patch that is present but over the size budget, or unreadable, is a
        // 200 saying so — the run exists and the operator's question was answered.
        if (preview.PatchMissing)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(preview.ToResponse(), ct);
    }
}
