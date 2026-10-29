namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

/// <summary>
///     One workflow-designer turn: the model replies and, when a change was asked for, proposes a whole graph the node has
///     validated and linted. Writes nothing; accepting and saving are the operator's.
/// </summary>
/// <remarks>
///     400 for bad input or an ineligible model, 403 <c>CloudLocalDataWithheld</c> when a cloud model may not read the
///     workflow, 409 <c>NodeBusy</c> while a run or a draft holds the node, 422 <c>Unparseable</c> when the model produced
///     nothing usable, 502 <c>ProviderFailed</c> when the model provider failed the request. An invalid proposal is still a 200, with <c>validation.valid</c> false.
/// </remarks>
public sealed class RunGraphWorkflowDesignerTurnEndpoint : Endpoint<RunGraphWorkflowDesignerTurnRequest, GraphWorkflowDesignerTurnResponse>
{
    private readonly IGraphWorkflowDesignerService _designer;

    public RunGraphWorkflowDesignerTurnEndpoint(IGraphWorkflowDesignerService designer)
    {
        ArgumentNullException.ThrowIfNull(designer);
        _designer = designer;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.DesignerTurn);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.Accepts<RunGraphWorkflowDesignerTurnRequest>("application/json")
                                             .Produces<GraphWorkflowDesignerTurnResponse>()
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces<GraphWorkflowDesignerErrorResponse>(StatusCodes.Status403Forbidden)
                                             .Produces<GraphWorkflowDesignerErrorResponse>(StatusCodes.Status409Conflict)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                                             .Produces<GraphWorkflowDesignerErrorResponse>(StatusCodes.Status422UnprocessableEntity)
                                             .Produces<GraphWorkflowDesignerErrorResponse>(StatusCodes.Status502BadGateway));
    }

    public override async Task HandleAsync(RunGraphWorkflowDesignerTurnRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.IsOversized(HttpContext.Request))
        {
            await Send.ResultAsync(RequestBodyTooLargeProblem.Result(GraphWorkflowRequestSizeLimit.OversizedDetail));
            return;
        }

        // Mapped even with no nodes: an empty canvas still carries the graph kind and chat block a proposal is compared against.
        var graphJson = GraphWorkflowContractMapper.ToGraphJson(req.Graph);
        var result = await _designer.TurnAsync(new GraphWorkflowDesignerTurnRequest
            {
                DefinitionId = req.DefinitionId,
                ModelName = req.ModelName,
                Messages =
                [
                    .. req.Messages.Select(static message => new GraphWorkflowDesignerMessage
                    {
                        Role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) ? GraphWorkflowDesignerRole.User : GraphWorkflowDesignerRole.Assistant,
                        Text = message.Text
                    })
                ],
                GraphJson = graphJson,
                SelectedNodeKey = req.SelectedNodeKey,
                Intent = Enum.TryParse<GraphWorkflowDesignerIntent>(req.Intent, ignoreCase: true, out var intent) ? intent : GraphWorkflowDesignerIntent.Auto
            },
            ct);

        if (result.Turn is not { } turn)
        {
            var message = result.FailureMessage ?? "The designer turn could not be produced.";
            var typed = result.Failure switch
            {
                GraphWorkflowDesignerFailureKind.NodeBusy => Results.Conflict(Error(GraphWorkflowDesignerErrorCode.NodeBusy, message, grant: null)),
                GraphWorkflowDesignerFailureKind.Unparseable => Results.UnprocessableEntity(Error(GraphWorkflowDesignerErrorCode.Unparseable, message, grant: null)),
                GraphWorkflowDesignerFailureKind.CloudLocalDataWithheld => Results.Json(Error(GraphWorkflowDesignerErrorCode.CloudLocalDataWithheld, message, "LocalData"),
                    statusCode: StatusCodes.Status403Forbidden),
                GraphWorkflowDesignerFailureKind.ProviderFailed => Results.Json(Error(GraphWorkflowDesignerErrorCode.ProviderFailed, message, grant: null),
                    statusCode: StatusCodes.Status502BadGateway),
                _ => null
            };
            if (typed is not null)
            {
                await Send.ResultAsync(typed);
                return;
            }

            AddError(message);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var proposalGraph = turn.ProposalGraphJson is { } proposal ? WithIdentityStrings(GraphWorkflowContractMapper.ToWireGraph(proposal)) : null;
        await Send.OkAsync(new GraphWorkflowDesignerTurnResponse
            {
                Reply = turn.Reply,
                Proposal = proposalGraph,
                Changes =
                [
                    .. turn.Changes.Select(static change => new GraphWorkflowDesignerChangeResponse
                    {
                        Kind = change.Kind,
                        Key = change.Key,
                        Change = change.Change
                    })
                ],
                Validation = turn.Validation is { } validation ? ToResponse(validation, proposalGraph) : null,
                Attempts = turn.Attempts,
                GenerationMetadata = new GraphWorkflowDesignerGenerationMetadata
                {
                    Model = turn.Model,
                    GeneratedAtUtc = turn.GeneratedAtUtc.ToUnixTimeMilliseconds(),
                    DurationMs = turn.DurationMs,
                    InputTokens = turn.InputTokens,
                    OutputTokens = turn.OutputTokens
                }
            },
            ct);
    }

    /// <summary>
    ///     The proposal with every non-optional string of a node or edge filled in, and null arrays and elements dropped: a
    ///     model without a strict schema can leave either, and the client's contract refuses both. Validation still reports it.
    /// </summary>
    private static GraphWorkflowGraph WithIdentityStrings(GraphWorkflowGraph graph) =>
        graph with
        {
            Nodes =
            [
                .. (graph.Nodes ?? []).OfType<GraphWorkflowGraphNode>()
                                      .Select(static node => node with { Key = node.Key ?? string.Empty, Kind = node.Kind ?? string.Empty })
            ],
            Edges =
            [
                .. (graph.Edges ?? []).OfType<GraphWorkflowGraphEdge>()
                                      .Select(static edge => edge with { Key = edge.Key ?? string.Empty, From = edge.From ?? string.Empty, To = edge.To ?? string.Empty })
            ]
        };

    private static GraphWorkflowDesignerErrorResponse Error(GraphWorkflowDesignerErrorCode code, string message, string? grant) =>
        new()
        {
            Code = code,
            Message = message,
            Grant = grant
        };

    /// <summary>The validate endpoint's report shape, counted off the proposal as that endpoint counts off the authored graph.</summary>
    private static ValidateGraphWorkflowDefinitionResponse ToResponse(GraphWorkflowValidationResult validation, GraphWorkflowGraph? proposal) =>
        new()
        {
            Valid = validation.IsValid,
            Errors = [.. validation.Errors.Select(ToResponse)],
            NodeCount = proposal?.Nodes.Count ?? 0,
            Warnings = [.. validation.Warnings.Select(ToResponse)]
        };

    private static GraphWorkflowValidationErrorResponse ToResponse(GraphWorkflowValidationError error) =>
        new()
        {
            Key = error.Key,
            Message = error.Message
        };
}
