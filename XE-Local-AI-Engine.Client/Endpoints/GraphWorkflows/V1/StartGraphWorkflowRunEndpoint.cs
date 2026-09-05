namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Starts a run of one definition. 202, not 200: the endpoint commits a durable intent and the dispatcher advances
///     it out of band, so the run legitimately reads <c>Pending</c> the moment this answers.
///     <para>
///         The caller's <c>requestId</c> is the idempotency key. The same one always answers with the same
///         <c>runId</c> — no second run, no conflict — which is what lets a scheduler or an integration retry a start
///         it never saw the answer to.
///     </para>
/// </summary>
public sealed class StartGraphWorkflowRunEndpoint(IGraphWorkflowRunService runs) : Endpoint<StartGraphWorkflowRunRequest, StartGraphWorkflowRunResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.DefinitionRuns);
        Policies(NodeAuthorizationPolicies.Operator);

        // The same body cap the three graph routes carry: a run input is bounded by MaxRunInputBytes in the service,
        // and this is what stops a body far past that from being read at all.
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.Produces<StartGraphWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails()
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(StartGraphWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.RefuseIfOversized(HttpContext.Request, this))
        {
            await Send.ErrorsAsync(StatusCodes.Status413PayloadTooLarge, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // Re-serialized rather than passed as the raw body slice: what the run stores has to be the value of the
            // `input` member, not the request that carried it.
            var input = req.Input is { } payload ? JsonSerializer.Serialize(payload) : null;
            var detail = await _runs.StartAsync(req.DefinitionId, req.RequestId, input, req.DefinitionVersion, ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: new StartGraphWorkflowRunResponse(detail.Run.Id))).ConfigureAwait(false);
        }
        catch (GraphWorkflowValidationException exception)
        {
            // Replayed here rather than in the global single-message handler, for the same reason the definition
            // routes do it: a graph that cannot start says every wrong thing about itself at once.
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
