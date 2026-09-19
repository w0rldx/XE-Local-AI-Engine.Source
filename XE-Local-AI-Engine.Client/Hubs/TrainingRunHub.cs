namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public static class TrainingRunHubEvents
{
    public const string Event = "trainingRun.event";
    public const string ReplayReset = "trainingRun.replayReset";
}

public sealed class TrainingRunReplayReset
{
    public required Guid RunId { get; init; }

    public required long LatestSequence { get; init; }

    public required long RunVersion { get; init; }
}

/// <summary>
///     Operator-only, per-run delivery for live training progress. The caller joins the group before replay so the
///     subscribe-after-publish race closes; the overlap is deduplicated client-side by event sequence.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class TrainingRunHub : Hub
{
    private readonly ITrainingRunEventBuffer _events;
    private readonly ITrainingRunService _runs;

    public TrainingRunHub(ITrainingRunService runs, ITrainingRunEventBuffer events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(runs);
        _events = events;
        _runs = runs;
    }

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
        var run = await _runs.GetAsync(runId, cancellationToken)
                  ?? throw new HubException("The training run was not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId), cancellationToken);

        var replay = _events.Replay(runId, afterSeq);
        if (replay.ResetRequired)
        {
            await Clients.Caller.SendAsync(TrainingRunHubEvents.ReplayReset,
                             new TrainingRunReplayReset { RunId = runId, LatestSequence = replay.LatestSequence, RunVersion = run.Version },
                             cancellationToken);
            return;
        }

        foreach (var runEvent in replay.Events)
        {
            await Clients.Caller.SendAsync(TrainingRunHubEvents.Event, runEvent, cancellationToken);
        }
    }

    public Task Unsubscribe(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId), Context.ConnectionAborted);
}
