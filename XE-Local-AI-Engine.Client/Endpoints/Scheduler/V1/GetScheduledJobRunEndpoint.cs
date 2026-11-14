namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Scheduler;

public sealed class GetScheduledJobRunEndpoint : Endpoint<ScheduledJobRunRouteRequest, ScheduledJobRunResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    private readonly IScheduledJobManagementService _scheduledJobManagementService;

    public GetScheduledJobRunEndpoint(IScheduledJobManagementService scheduledJobManagementService, INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(scheduledJobManagementService);
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _scheduledJobManagementService = scheduledJobManagementService;
        _chatPersistence = chatPersistence;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Scheduler.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ScheduledJobRunRouteRequest req, CancellationToken ct)
    {
        var record = await _scheduledJobManagementService.GetRunAsync(req.RunId, ct);
        // A successful run-agent fire stores its answer at the run's id (ADR 0024); only a succeeded run links it.
        var hasAnswer = record.Status.ToWire() == ScheduledRunStatus.Succeeded
                        && string.Equals(await _chatPersistence.GetConversationKindAsync(record.Id, ct), NodeConversationKind.Scheduled, StringComparison.Ordinal);
        await Send.OkAsync(record.ToResponse(hasAnswer ? record.Id : null), ct);
    }
}
