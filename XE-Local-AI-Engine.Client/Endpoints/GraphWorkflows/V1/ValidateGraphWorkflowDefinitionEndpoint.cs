namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Judges a graph without saving it, so the editor asks the RUNTIME's own parser whether a graph would route and
///     draws the answer a run would get rather than a second implementation of the same rules. Persists nothing.
///     <para>
///         Answers 200 for any well-formed body: a validation report is not a request failure, and the client needs
///         the same shape whether there are zero errors or five.
///     </para>
/// </summary>
public sealed class ValidateGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    : Endpoint<ValidateGraphWorkflowDefinitionRequest, ValidateGraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.DefinitionsValidate);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ValidateGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = _definitions.Validate(GraphWorkflowContractMapper.ToGraphJson(req.Graph));

        // Counted off the AUTHORED document rather than off the parse, so the number is defined for a graph the parser
        // refused too — which is the case the editor most needs it in, to say how far over the cap the canvas is.
        var nodeCount = req.Graph.Nodes?.Count ?? 0;
        await Send.OkAsync(new ValidateGraphWorkflowDefinitionResponse(result.IsValid,
                [.. result.Errors.Select(static error => new GraphWorkflowValidationErrorResponse(error.Key, error.Message))],
                nodeCount),
            ct).ConfigureAwait(false);
    }
}
