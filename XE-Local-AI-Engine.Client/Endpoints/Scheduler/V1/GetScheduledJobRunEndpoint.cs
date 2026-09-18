namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class GetScheduledJobRunEndpoint : Endpoint<ScheduledJobRunRouteRequest, ScheduledJobRunResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public GetScheduledJobRunEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ScheduledJobRunRouteRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.GetRunAsync(req.RunId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
