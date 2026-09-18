namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class GetScheduledJobEndpoint : Endpoint<ScheduledJobRouteRequest, ScheduledJobResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public GetScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ScheduledJobRouteRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.GetJobAsync(req.ScheduledJobId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
