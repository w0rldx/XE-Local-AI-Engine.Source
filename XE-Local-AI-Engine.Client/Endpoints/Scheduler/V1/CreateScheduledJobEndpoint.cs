namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class CreateScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<CreateScheduledJobRequest, ScheduledJobResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Scheduler.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateScheduledJobRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.CreateJobAsync(req.ToInput(), ct).ConfigureAwait(false);
        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
