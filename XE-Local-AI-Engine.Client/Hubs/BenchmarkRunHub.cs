namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public static class BenchmarkRunHubEvents
{
    public const string Event = "benchmarkRun.event";
    public const string ReplayReset = "benchmarkRun.replayReset";
}

public sealed record BenchmarkRunReplayReset(Guid RunId, long LatestSequence, long RunVersion);

/// <summary>
///     Operator-only, per-run delivery for transient benchmark output. Joining the group before replay closes the
///     subscribe-after-publish race; clients deduplicate the possible overlap by the event sequence.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class BenchmarkRunHub(IBenchmarkStore store, IBenchmarkEventBuffer events) : Hub
{
    private readonly IBenchmarkEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static string RunGroup(Guid runId) =>
        $"benchmark-run-{runId:N}";

    public async Task Subscribe(Guid runId, long afterSeq)
    {
        if (runId == Guid.Empty)
        {
            throw new HubException("Benchmark run id is required.");
        }

        if (afterSeq < -1)
        {
            throw new HubException("Benchmark replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)
                  ?? throw new HubException("Benchmark run was not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId), cancellationToken).ConfigureAwait(false);

        var replay = _events.Replay(runId, afterSeq, run.Version);
        var latestSequence = Math.Max(replay.LatestSequence, run.LastStreamSequence);
        if (replay.ResetRequired || run.LastStreamSequence > replay.LatestSequence)
        {
            await Clients.Caller.SendAsync(BenchmarkRunHubEvents.ReplayReset,
                             new BenchmarkRunReplayReset(runId, latestSequence, run.Version),
                             cancellationToken)
                         .ConfigureAwait(false);
            return;
        }

        foreach (var streamEvent in replay.Events)
        {
            await Clients.Caller.SendAsync(BenchmarkRunHubEvents.Event, streamEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task Unsubscribe(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId));
}
