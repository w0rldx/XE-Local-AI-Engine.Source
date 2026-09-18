namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class UpdateScheduledJobEndpoint : Endpoint<UpdateScheduledJobRequest, ScheduledJobResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public UpdateScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Scheduler.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateScheduledJobRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.UpdateJobAsync(req.ScheduledJobId, req.ToInput(), ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
