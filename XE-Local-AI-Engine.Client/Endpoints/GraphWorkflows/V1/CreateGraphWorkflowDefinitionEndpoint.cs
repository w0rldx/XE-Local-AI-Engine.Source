namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Creates a definition, refusing a graph the dispatcher could not route.
///     <para>
///         Validated by the RUNTIME's own parser rather than by a validator of this endpoint's own: it is the same
///         parser run start uses, so a graph accepted here is one that will start, and a rule added there cannot be
///         forgotten here. Its refusal carries EVERY failure, each keyed to the node or edge it belongs to, so the
///         exception is replayed here rather than in the global single-message handler that could only report one.
///     </para>
/// </summary>
public sealed class CreateGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    : Endpoint<CreateGraphWorkflowDefinitionRequest, GraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 200 only would type the one status it never answers.
        Description(static builder => builder.Produces<GraphWorkflowDefinitionResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(CreateGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.RefuseIfOversized(HttpContext.Request, this))
        {
            await Send.ErrorsAsync(StatusCodes.Status413PayloadTooLarge, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var graphJson = GraphWorkflowContractMapper.ToGraphJson(req.Graph);
            var created = await _definitions.CreateAsync(req.Name, req.Description, graphJson, ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetGraphWorkflowDefinitionEndpoint>(new
                {
                    definitionId = created.Id
                },
                created.ToResponse(),
                cancellation: ct).ConfigureAwait(false);
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
