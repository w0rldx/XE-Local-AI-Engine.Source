namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>One definition in full, graph included — what the editor opens.</summary>
public sealed class GetGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions) : Endpoint<GraphWorkflowDefinitionRequest, GraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definition = await _definitions.GetAsync(req.DefinitionId, ct);
        await Send.OkAsync(definition.ToResponse(), ct);
    }
}
