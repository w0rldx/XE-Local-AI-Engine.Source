namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Archives a definition rather than deleting it: every run that pinned it keeps rendering, and a definition
///     cannot become permanently undeletable because a year-old run still references it.
/// </summary>
/// <remarks>It disappears from the picker and from the default list.</remarks>
public sealed class ArchiveDevWorkflowDefinitionEndpoint : Endpoint<DevWorkflowDefinitionRequest>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public ArchiveDevWorkflowDefinitionEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        _ = await _authoring.ArchiveDefinitionAsync(req.DefinitionId, ct);
        await Send.NoContentAsync(ct);
    }
}
