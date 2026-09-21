namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class UpdateDevWorkflowDefinitionEndpoint : Endpoint<UpdateDevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    private readonly DevWorkflowOptions _options;

    public UpdateDevWorkflowDefinitionEndpoint(DevWorkflowAuthoringService authoring, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new DevWorkflowRequestSizeLimit()));
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateDevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (DevWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(DevWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        // A null graph leaves the stored one alone — a rename must not have to echo a graph back to keep it. Runs that
        // already pinned this definition are unaffected either way: they carry their own snapshot.
        string? graphJson = null;
        int? nodeCount = null;
        if (req.Graph is { } graph)
        {
            graphJson = DevWorkflowContractMapper.ToGraphJson(graph);
            nodeCount = DevWorkflowGraphContract.ValidateAndCountNodes(graphJson, _options.MaxNodesPerDefinition);
        }

        var updated = await _authoring.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand { DefinitionId = req.DefinitionId, ExpectedVersion = req.Version, Name = req.Name, GraphJson = graphJson, NodeCount = nodeCount }, ct);
        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
