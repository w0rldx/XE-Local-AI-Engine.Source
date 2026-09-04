namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     Requests cancellation of an execution from the admin surface. The SAME primitive the external route calls, so
///     the durable stop marker, the terminal transaction and the in-process signal cannot drift between the two.
///     <para>
///         Unlike the external route this one is operator-scoped and does NOT go through the key-scoped access helper:
///         an operator cancelling from the admin UI must be able to reach every row, whichever integrator owns it.
///     </para>
/// </summary>
public sealed class CancelIntegrationExecutionEndpoint(IntegrationExecutionQueryService executions)
    : EndpointWithoutRequest
{
    private readonly IntegrationExecutionQueryService _executions = executions ?? throw new ArgumentNullException(nameof(executions));

    public override void Configure()
    {
        Post(LocalApiRoutes.Integrations.ExecutionCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // The three codes HandleAsync actually answers. Without this the generated client declares the framework's
        // default 204 alone — a contract lie that hides the 409 a caller has to branch on and promises a code this
        // endpoint never sends. The 409 is declared as FastEndpoints' OWN problem shape because that is what
        // AddError + Send.ErrorsAsync writes (see ProblemDetailsProducesExtensions); the 202 and 404 carry no body.
        // ClearDefaultProduces takes the ONE code to drop: the bare overload clears everything, which silently takes
        // the 401 and 403 the auth policy contributes with it.
        Description(builder => builder.ClearDefaultProduces(StatusCodes.Status204NoContent)
                                      .Produces(StatusCodes.Status202Accepted)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        switch (await _executions.RequestCancelAsync(Route<Guid>("executionId"), ct).ConfigureAwait(false))
        {
            case IntegrationCancelOutcome.Requested:
                // 202, not 204: cancellation is REQUESTED here. A running turn stops when its token is observed, and
                // the coordinator writes the terminal row.
                await Send.ResultAsync(TypedResults.Accepted((string?)null)).ConfigureAwait(false);
                return;
            case IntegrationCancelOutcome.AlreadyTerminal:
                AddError("The execution has already finished.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct).ConfigureAwait(false);
                return;
            default:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
        }
    }
}
