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
                await Send.ResultAsync(Results.Conflict(new ScheduledJobRunCancelResponse
                {
                    Outcome = outcome.ToString()
                })).ConfigureAwait(false);
                return;

            default:
                // Requested / RequestedButNotRunning — the request was recorded; report the stamped timestamp.
                var run = await _scheduledJobManagementService.GetRunAsync(req.RunId, ct).ConfigureAwait(false);
                await Send.ResultAsync(Results.Accepted(null, new ScheduledJobRunCancelResponse
                {
                    Outcome = outcome.ToString(),
                    CancellationRequestedAtUtc = run?.CancellationRequestedAtUtc
                })).ConfigureAwait(false);
                return;
        }
    }
}
