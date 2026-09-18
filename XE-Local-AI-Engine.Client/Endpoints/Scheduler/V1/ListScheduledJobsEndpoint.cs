namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class ListScheduledJobsEndpoint : Endpoint<ListScheduledJobsRequest, ListScheduledJobsResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public ListScheduledJobsEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListScheduledJobsRequest req, CancellationToken ct)
    {
        var records = await _scheduledJobManagementService.ListJobsAsync(req.IncludeDeleted, ct);
        await Send.OkAsync(new ListScheduledJobsResponse
            {
                Items = [.. records.Select(static r => r.ToResponse())]
            },
            ct);
    }
}
