namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Server-push hub for image-job progress, protected with the same Operator policy as the other local hubs.
/// </summary>
/// <remarks>
///     Clients drive jobs through REST and receive coarse status transitions (each carrying its jobId + monotonic seq)
///     via <see cref="ImageJobEventPublisher" />. <see cref="Subscribe" /> opts a connection into a per-job group THEN
///     replays that job's buffered events — join-then-replay closes the subscribe-after-publish race, and the client
///     dedupes an event delivered both via replay (Caller) and live (Group) on the payload's <c>seq</c>. A disconnect
///     does NOT cancel the job: image generation is durable and outlives the tab.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class ImageJobHub : Hub
{
    private readonly IImageJobCoordinator _coordinator;

    public ImageJobHub(IImageJobCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    /// <summary>Returns the SignalR group name for a job's scoped delivery.</summary>
    public static string JobGroup(Guid jobId)
    {
        return $"image-job-{jobId:N}";
    }

    /// <summary>
    ///     Opts this connection into the per-job group so it receives only that job's scoped events, THEN replays the
    ///     job's buffered events to the caller.
    /// </summary>
    public async Task Subscribe(Guid jobId)
    {
        var ct = Context.ConnectionAborted;
        await Groups.AddToGroupAsync(Context.ConnectionId, JobGroup(jobId), ct);

        foreach (var bufferedEvent in _coordinator.SnapshotBufferedEvents(jobId))
        {
            await Clients.Caller.SendAsync(bufferedEvent.MethodName, bufferedEvent.Payload, ct);
        }
    }

    /// <summary>Removes this connection from a job's group (e.g. when the gallery closes a job view).</summary>
    public Task Unsubscribe(Guid jobId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroup(jobId), Context.ConnectionAborted);
    }
}
