namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     FastEndpoints handler for listing all registered scheduler job templates (GET scheduler/templates).
/// </summary>
public sealed class ListScheduledJobTemplatesEndpoint(IScheduledJobManagementService scheduledJobManagementService)
    : EndpointWithoutRequest<ListScheduledJobTemplatesResponse>
{
    private readonly IScheduledJobManagementService _scheduledJobManagementService = scheduledJobManagementService ?? throw new ArgumentNullException(nameof(scheduledJobManagementService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var descriptors = _scheduledJobManagementService.ListTemplatesAsync();
        await Send.OkAsync(new ListScheduledJobTemplatesResponse
            {
                Items = [.. descriptors.Select(static d => d.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
