namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for requesting cancellation of a running job run
///     (POST scheduler/runs/{runId}/cancel). Cancellation is best-effort: 404 when the run is missing, 409 when it has
///     already reached a terminal state, and 202 Accepted (with the outcome) when the request was recorded — whether or
///     not Quartz had an active fire to interrupt. Operator-gated.
/// </summary>
public sealed class CancelScheduledJobRunEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<ScheduledJobRunRouteRequest, ScheduledJobRunCancelResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Scheduler.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST (run id from the route, no body): override the default application/json-only Accepts so a
        // body-less request is not rejected with 415 (see TriggerScheduledJobEndpoint for the full rationale).
        Description(x => x.Accepts<ScheduledJobRunRouteRequest>()
                          .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ScheduledJobRunRouteRequest req, CancellationToken ct)
    {
        var outcome = await _scheduledJobManagementService.CancelRunAsync(req.RunId, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case RunCancellationOutcome.NotFound:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;

            case RunCancellationOutcome.AlreadyTerminal:
                // The outcome stays machine-readable as an `outcome` extension member so a client can branch on it
                // exactly as it did when the 409 carried a ScheduledJobRunCancelResponse body.
                await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: "The run already reached a terminal state and cannot be cancelled.",
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outcome"] = outcome.ToString()
                    })).ConfigureAwait(false);
                return;

            default:
                // Requested / RequestedButNotRunning — the request was recorded; report the stamped timestamp.
                var run = await _scheduledJobManagementService.GetRunAsync(req.RunId, ct).ConfigureAwait(false);
                await Send.ResultAsync(Results.Accepted(uri: null, new ScheduledJobRunCancelResponse
                {
                    Outcome = outcome.ToString(),
                    CancellationRequestedAtUtc = run?.CancellationRequestedAtUtc
                })).ConfigureAwait(false);
                return;
        }
    }
}
