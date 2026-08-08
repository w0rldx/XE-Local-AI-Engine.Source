namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class TriggerScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<ScheduledJobActionRequest>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Scheduler.JobTrigger);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the job id binds from the route, so a well-behaved client sends no body — and therefore no
        // Content-Type. The default POST "Accepts" metadata only allows application/json, which FastEndpoints answers
        // with 415 when the header is absent. Overriding Accepts lets the body-less "Run now" request through.
        Description(x => x.Accepts<ScheduledJobActionRequest>());
    }

    public override async Task HandleAsync(ScheduledJobActionRequest req, CancellationToken ct)
    {
        try
        {
            await _scheduledJobManagementService.TriggerNowAsync(req.ScheduledJobId, parameterOverrides: null, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (ScheduledJobValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
