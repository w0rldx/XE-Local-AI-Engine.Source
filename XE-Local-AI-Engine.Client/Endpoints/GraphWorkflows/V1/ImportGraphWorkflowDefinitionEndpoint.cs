namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>Creates a definition from a share file, through the same service call, parser and tool gate as create.</summary>
/// <remarks>
///     The validator refuses a foreign format or version before the graph is read; everything after that is create's
///     path, so an imported graph is held to exactly the rules a drawn one is.
/// </remarks>
public sealed class ImportGraphWorkflowDefinitionEndpoint : Endpoint<ImportGraphWorkflowDefinitionRequest, GraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions;

    public ImportGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.DefinitionsImport);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.Produces<GraphWorkflowDefinitionResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(ImportGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(GraphWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        try
        {
            var graphJson = GraphWorkflowContractMapper.ToGraphJson(req.Graph);
            var created = await _definitions.CreateAsync(req.Name, req.Description, graphJson, ct);
            await Send.CreatedAtAsync<GetGraphWorkflowDefinitionEndpoint>(new
                {
                    definitionId = created.Id
                },
                created.ToResponse(),
                cancellation: ct);
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
