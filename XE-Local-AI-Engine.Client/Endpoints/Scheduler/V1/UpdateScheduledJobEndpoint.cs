namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for updating a scheduled job definition (PUT scheduler/jobs/{scheduledJobId}).
/// </summary>
public sealed class UpdateScheduledJobEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : Endpoint<UpdateScheduledJobRequest, ScheduledJobResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Scheduler.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateScheduledJobRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _scheduledJobManagementService.UpdateJobAsync(req.ScheduledJobId, req.ToInput(), ct).ConfigureAwait(false);
            if (record is null)
            {
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (ScheduledJobValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
