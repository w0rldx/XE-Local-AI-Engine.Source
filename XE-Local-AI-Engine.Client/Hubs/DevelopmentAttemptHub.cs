namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed record DevelopmentAttemptSubscriptionSnapshot(
    Guid ProjectId,
    Guid TaskId,
    Guid AttemptId,
    long Watermark,
    long DroppedOrCoalescedUpdateCount,
    DevelopmentAttemptLiveUpdate? Latest);

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class DevelopmentAttemptHub(
    IDevelopmentManagementService managementService,
    IDevelopmentAttemptLiveBroker broker) : Hub
{
    public async Task<DevelopmentAttemptSubscriptionSnapshot> SubscribeAsync(Guid projectId,
        Guid taskId,
        Guid attemptId)
    {
        var task = await managementService.GetTaskAsync(projectId, taskId, Context.ConnectionAborted).ConfigureAwait(false);
        var attempt = task.Attempts.SingleOrDefault(candidate => candidate.Id == attemptId)
                      ?? throw new HubException("The Development attempt does not belong to the requested project and task.");
        if (attempt.Status is not (DevelopmentAttemptStatus.Pending or DevelopmentAttemptStatus.Running))
        {
            throw new HubException("Only the current active Development attempt can be subscribed.");
        }

        if (!broker.TryGetSnapshot(attemptId, out _))
        {
            throw new HubException("The Development attempt has no active live stream.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId,
            DevelopmentAttemptHubGroups.Attempt(projectId, attemptId),
            Context.ConnectionAborted).ConfigureAwait(false);
        if (!broker.TryGetSnapshot(attemptId, out var snapshot))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId,
                DevelopmentAttemptHubGroups.Attempt(projectId, attemptId),
                Context.ConnectionAborted).ConfigureAwait(false);
            throw new HubException("The Development attempt completed while the subscription was being established.");
        }

        return new DevelopmentAttemptSubscriptionSnapshot(projectId,
            taskId,
            attemptId,
            snapshot.Watermark,
            snapshot.DroppedOrCoalescedUpdateCount,
            snapshot.Latest);
    }
}

internal static class DevelopmentAttemptHubGroups
{
    public static string Attempt(Guid projectId, Guid attemptId) =>
        string.Concat("development-project:", projectId.ToString("N"), ":attempt:", attemptId.ToString("N"));
}
