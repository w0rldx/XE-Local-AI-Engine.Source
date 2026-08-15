namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class EnableScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<ScheduledJobActionRequest, ScheduledJobResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Scheduler.JobEnable);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST (job id from the route, no body): override the default application/json-only Accepts so a
        // body-less request is not rejected with 415 (see TriggerScheduledJobEndpoint for the full rationale).
        Description(x => x.Accepts<ScheduledJobActionRequest>());
    }

    public override async Task HandleAsync(ScheduledJobActionRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _scheduledJobManagementService.SetEnabledAsync(req.ScheduledJobId, enabled: true, ct).ConfigureAwait(false);
            if (record is null)
            {
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (ScheduledJobValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
