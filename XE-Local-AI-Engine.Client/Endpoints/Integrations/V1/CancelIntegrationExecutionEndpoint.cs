namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     Requests cancellation of an execution from the admin surface, through the SAME primitive the external route
///     calls, so the durable stop marker, the terminal transaction and the in-process signal cannot drift.
/// </summary>
/// <remarks>
///     Unlike the external route this one is operator-scoped and does NOT go through the key-scoped access helper: an
///     operator cancelling from the admin UI must be able to reach every row, whichever integrator owns it.
/// </remarks>
public sealed class CancelIntegrationExecutionEndpoint : EndpointWithoutRequest
{
    private readonly IntegrationExecutionQueryService _executions;

    public CancelIntegrationExecutionEndpoint(IntegrationExecutionQueryService executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        _executions = executions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Integrations.ExecutionCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // The three codes HandleAsync actually answers: without this the generated client declares the framework's default 204 alone, hiding the 409 a caller must branch on.
        // The 409 is FastEndpoints' OWN problem shape (see ProblemDetailsProducesExtensions); ClearDefaultProduces takes the ONE code to drop, since the bare overload clears the 401 and 403 too.
        Description(builder => builder.ClearDefaultProduces(StatusCodes.Status204NoContent)
                                      .Produces(StatusCodes.Status202Accepted)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        switch (await _executions.RequestCancelAsync(Route<Guid>("executionId"), ct))
        {
            case IntegrationCancelOutcome.Requested:
                // 202, not 204: cancellation is REQUESTED here. A running turn stops when its token is observed, and
                // the coordinator writes the terminal row.
                await Send.ResultAsync(TypedResults.Accepted((string?)null));
                return;
            case IntegrationCancelOutcome.AlreadyTerminal:
                AddError("The execution has already finished.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            default:
                await Send.NotFoundAsync(ct);
                return;
        }
    }
}
