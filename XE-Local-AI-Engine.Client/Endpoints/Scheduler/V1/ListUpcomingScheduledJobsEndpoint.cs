namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>The next automatic fire of each enabled scheduled job, soonest first. Operator-gated.</summary>
public sealed class ListUpcomingScheduledJobsEndpoint : Endpoint<ListUpcomingScheduledJobsRequest, ListUpcomingScheduledJobsResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public ListUpcomingScheduledJobsEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.Upcoming);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListUpcomingScheduledJobsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var upcoming = await _scheduledJobManagementService.ListUpcomingAsync(req.Limit ?? ListUpcomingScheduledJobsRequestValidator.DefaultLimit, ct);
        await Send.OkAsync(new ListUpcomingScheduledJobsResponse
            {
                Items = [.. upcoming.Select(static u => u.ToResponse())]
            },
            ct);
    }
}
