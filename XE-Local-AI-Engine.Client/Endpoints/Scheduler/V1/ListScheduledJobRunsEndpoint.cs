namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class ListScheduledJobRunsEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<ListScheduledJobRunsRequest, ListScheduledJobRunsResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListScheduledJobRunsRequest req, CancellationToken ct)
    {
        var records = await _scheduledJobManagementService.ListRunsAsync(req.Status.ToPersistence(), req.FromUtc, req.ToUtc, req.ScheduledJobId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListScheduledJobRunsResponse
            {
                Items = [.. records.Select(static r => r.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
