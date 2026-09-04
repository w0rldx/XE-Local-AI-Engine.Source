namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using FluentValidation.Results;
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
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

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
            foreach (var error in exception.Result.Errors)
            {
                // The node or edge key becomes the failure's property name, which is what lets the editor draw the
                // complaint on the offending element. A whole-document failure has no element, so it goes to
                // FastEndpoints' own general-errors field rather than to a key nothing on the canvas answers to.
                if (error.Key is { } key)
                {
                    AddError(new ValidationFailure(key, error.Message));
                }
                else
                {
                    AddError(error.Message);
                }
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
