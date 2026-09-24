namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentAttemptSubscriptionSnapshot
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required long Watermark { get; init; }

    public required long DroppedOrCoalescedUpdateCount { get; init; }

    public required DevelopmentAttemptLiveUpdate? Latest { get; init; }
}

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class DevelopmentAttemptHub : Hub
{
    private readonly IDevelopmentManagementService _managementService;
    private readonly IDevelopmentAttemptLiveBroker _broker;

    public DevelopmentAttemptHub(IDevelopmentManagementService managementService,
        IDevelopmentAttemptLiveBroker broker)
    {
        _managementService = managementService;
        _broker = broker;
    }

    public async Task<DevelopmentAttemptSubscriptionSnapshot> SubscribeAsync(Guid projectId,
        Guid taskId,
        Guid attemptId)
    {
        // Translate the not-found family the way every other subscribing hub does. Without this the miss reached the
        // client as SignalR's generic invocation error, which says nothing about which of the three ids was wrong.
        DevelopmentTaskAggregate task;
        try
        {
            task = await _managementService.GetTaskAsync(projectId, taskId, Context.ConnectionAborted);
        }
        catch (DevelopmentNotFoundException)
        {
            throw new HubException("The Development project or task was not found.");
        }

        var attempt = task.Attempts.SingleOrDefault(candidate => candidate.Id == attemptId)
                      ?? throw new HubException("The Development attempt does not belong to the requested project and task.");
        if (attempt.Status is not (DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            throw new HubException("Only the current active Development attempt can be subscribed.");
        }

        if (!_broker.TryGetSnapshot(attemptId, out _))
        {
            throw new HubException("The Development attempt has no active live stream.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId,
            DevelopmentAttemptHubGroups.Attempt(projectId, attemptId),
            Context.ConnectionAborted);
        if (!_broker.TryGetSnapshot(attemptId, out var snapshot))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId,
                DevelopmentAttemptHubGroups.Attempt(projectId, attemptId),
                Context.ConnectionAborted);
            throw new HubException("The Development attempt completed while the subscription was being established.");
        }

        return new DevelopmentAttemptSubscriptionSnapshot
        {
            ProjectId = projectId,
            TaskId = taskId,
            AttemptId = attemptId,
            Watermark = snapshot.Watermark,
            DroppedOrCoalescedUpdateCount = snapshot.DroppedOrCoalescedUpdateCount,
            Latest = snapshot.Latest
        };
    }
}

internal static class DevelopmentAttemptHubGroups
{
    public static string Attempt(Guid projectId, Guid attemptId) =>
        string.Concat("development-project:", projectId.ToString("N"), ":attempt:", attemptId.ToString("N"));
}
