namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class DeleteScheduledJobEndpoint : Endpoint<ScheduledJobRouteRequest>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public DeleteScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Scheduler.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ScheduledJobRouteRequest req, CancellationToken ct)
    {
        await _scheduledJobManagementService.DeleteJobAsync(req.ScheduledJobId, ct);
        await Send.NoContentAsync(ct);
    }
}
