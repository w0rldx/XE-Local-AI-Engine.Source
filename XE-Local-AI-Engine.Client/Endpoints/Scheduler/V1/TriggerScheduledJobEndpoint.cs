namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class TriggerScheduledJobEndpoint : Endpoint<ScheduledJobActionRequest>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public TriggerScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        _scheduledJobManagementService = scheduledJobManagementService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Scheduler.JobTrigger);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the job id binds from the route, so a well-behaved client sends no body and no Content-Type, which the default POST "Accepts" metadata answers
        // with 415. Overriding Accepts lets the body-less "Run now" request through.
        Description(x => x.Accepts<ScheduledJobActionRequest>());
    }

    public override async Task HandleAsync(ScheduledJobActionRequest req, CancellationToken ct)
    {
        // A missing or soft-deleted job is a 404 like the other job ACTION routes (GET still reads a deleted job); the service's own not-found check stays a 400 for its other callers.
        var job = await _scheduledJobManagementService.GetJobAsync(req.ScheduledJobId, ct);
        if (job is null || job.DeletedAtUtc is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await _scheduledJobManagementService.TriggerNowAsync(req.ScheduledJobId, parameterOverrides: null, ct);
        await Send.NoContentAsync(ct);
    }
}
