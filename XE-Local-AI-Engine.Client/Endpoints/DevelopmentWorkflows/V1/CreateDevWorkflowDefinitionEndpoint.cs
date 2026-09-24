namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Creates a definition, refusing a graph the dispatcher could not route.
/// </summary>
/// <remarks>
///     Validated by the RUNTIME's parser rather than by a validator of this endpoint's own: it is the same parser run
///     start uses, so a graph accepted here is one that will start, and a rule added there cannot be forgotten here.
///     Its refusal is a single message, and the global validation handler shapes it into the same 400 body every other
///     domain refusal produces.
/// </remarks>
public sealed class CreateDevWorkflowDefinitionEndpoint : Endpoint<CreateDevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    private readonly DevWorkflowOptions _options;

    public CreateDevWorkflowDefinitionEndpoint(DevWorkflowAuthoringService authoring, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new DevWorkflowRequestSizeLimit()));
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 400-only would type no success body at all.
        Description(static builder => builder.Produces<DevWorkflowDefinitionResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(CreateDevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (DevWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(DevWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        var graphJson = DevWorkflowContractMapper.ToGraphJson(req.Graph);
        var nodeCount = DevWorkflowGraphContract.ValidateAndCountNodes(graphJson, _options.MaxNodesPerDefinition);
        var created = await _authoring.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand
        {
            DefinitionId = Guid.NewGuid(),
            Name = req.Name,
            GraphJson = graphJson,
            NodeCount = nodeCount
        }, ct);
        await Send.CreatedAtAsync<GetDevWorkflowDefinitionEndpoint>(new
            {
                definitionId = created.Id
            },
            created.ToResponse(),
            cancellation: ct);
    }
}
