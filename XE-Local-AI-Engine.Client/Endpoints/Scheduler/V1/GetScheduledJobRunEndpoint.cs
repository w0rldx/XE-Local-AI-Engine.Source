namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for retrieving a single scheduled job run (GET scheduler/runs/{runId}).
/// </summary>
public sealed class GetScheduledJobRunEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<ScheduledJobRunRouteRequest, ScheduledJobRunResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ScheduledJobRunRouteRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.GetRunAsync(req.RunId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
