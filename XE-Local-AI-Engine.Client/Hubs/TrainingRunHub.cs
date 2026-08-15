namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public static class TrainingRunHubEvents
{
    public const string Event = "trainingRun.event";
    public const string ReplayReset = "trainingRun.replayReset";
}

public sealed record TrainingRunReplayReset(Guid RunId, long LatestSequence, long RunVersion);

/// <summary>
///     Operator-only, per-run delivery for live training progress. The caller joins the group before replay so the
///     subscribe-after-publish race closes; the overlap is deduplicated client-side by event sequence.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class TrainingRunHub(ITrainingRunStore store, ITrainingRunEventBuffer events) : Hub
{
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ITrainingRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static string RunGroup(Guid runId) =>
        $"training-run-{runId:N}";

    public async Task Subscribe(Guid runId, long afterSeq)
    {
        if (runId == Guid.Empty)
        {
            throw new HubException("The run id is required.");
        }

        if (afterSeq < -1)
        {
            throw new HubException("The replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        var run = await _store.GetAsync(runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new HubException("The training run was not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId), cancellationToken).ConfigureAwait(false);

        var replay = _events.Replay(runId, afterSeq);
        if (replay.ResetRequired)
        {
            await Clients.Caller.SendAsync(TrainingRunHubEvents.ReplayReset,
                             new TrainingRunReplayReset(runId, replay.LatestSequence, run.Version),
                             cancellationToken)
                         .ConfigureAwait(false);
            return;
        }

        foreach (var runEvent in replay.Events)
        {
            await Clients.Caller.SendAsync(TrainingRunHubEvents.Event, runEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task Unsubscribe(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));
}
