namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Server-push hub for Open Canvas (Preview) run events. Clients connect, drive runs through the REST endpoints, and
///     receive node/run events (each carrying its runId) broadcast via <see cref="PreviewWorkflowEventPublisher" />.
///     <see cref="OnDisconnectedAsync" /> cancels every run owned by the disconnecting connection so an abandoned tab
///     does not keep a run burning compute. <see cref="Subscribe" /> opts a connection into a per-run group for scoped
///     delivery. Protected with the same Operator policy as the other local hubs.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class PreviewWorkflowHub(IPreviewWorkflowExecutionService executionService) : Hub
{
    private readonly IPreviewWorkflowExecutionService _executionService =
        executionService ?? throw new ArgumentNullException(nameof(executionService));

    /// <summary>Returns the SignalR group name for a run's scoped delivery.</summary>
    public static string RunGroup(Guid runId)
    {
        return $"preview-run-{runId:N}";
    }

    /// <summary>
    ///     Opts this connection into the per-run group so it receives only that run's scoped events, registers it as a
    ///     live subscriber (which is what keeps the run out of the abandoned-subscriber sweep), THEN replays the run's
    ///     buffered events with seq greater than <paramref name="afterSeq" /> to the caller. Join-then-replay (not
    ///     replay-then-join) closes the subscribe-after-publish race: events published between the two steps still
    ///     reach the group, and any event delivered both via replay (Caller) and live (Group) is deduped by the client
    ///     on the payload's monotonic <c>seq</c>.
    ///     <para>
    ///         <paramref name="afterSeq" /> is the highest seq the caller has already applied; <c>-1</c> (a fresh page,
    ///         e.g. after a reload) replays the whole retained log, which is how a reattaching client recovers a run
    ///         whose id it only knows from the route.
    ///     </para>
    /// </summary>
    public async Task Subscribe(Guid runId, long afterSeq)
    {
        var ct = Context.ConnectionAborted;
        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId), ct).ConfigureAwait(false);
        _executionService.AddSubscriber(runId, Context.ConnectionId);

        foreach (var bufferedEvent in _executionService.SnapshotBufferedEvents(runId, afterSeq))
        {
            await Clients.Caller.SendAsync(bufferedEvent.MethodName, bufferedEvent.Payload, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Removes this connection from a run's group and its subscriber set (e.g. when the canvas closes a run view).</summary>
    public Task Unsubscribe(Guid runId)
    {
        _executionService.RemoveSubscriber(runId, Context.ConnectionId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Drop this connection from every run's watcher set FIRST: a run it was the last watcher of now starts its
        // abandoned-subscriber grace period, which is what eventually reclaims a slot leaked by a page reload.
        // A run that additionally declared this connection as its owner is cancelled outright, as before.
        _executionService.RemoveSubscriberFromAllRuns(Context.ConnectionId);
        await _executionService.CancelRunsForConnectionAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }
}
