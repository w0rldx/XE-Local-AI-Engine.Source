namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>
///     Operator-only, per-run delivery for transient benchmark output. Joining the group before replay closes the
///     subscribe-after-publish race; clients deduplicate the possible overlap by the event sequence.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class BenchmarkRunHub : Hub
{
    private readonly IBenchmarkEventBuffer _events;
    private readonly BenchmarkRecordService _records;

    public BenchmarkRunHub(BenchmarkRecordService records, IBenchmarkEventBuffer events)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(records);
        _events = events;
        _records = records;
    }

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
        BenchmarkRunRecord run;
        try
        {
            run = await _records.GetRunAsync(runId, cancellationToken);
        }
        catch (BenchmarkNotFoundException exception)
        {
            // A hub has no exception handler: without this the caller would see SignalR\'s generic invocation error.
            throw new HubException("Benchmark run was not found.", exception);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(runId), cancellationToken);

        var replay = _events.Replay(runId, afterSeq, run.Version);
        var latestSequence = Math.Max(replay.LatestSequence, run.LastStreamSequence);
        if (replay.ResetRequired || run.LastStreamSequence > replay.LatestSequence)
        {
            await Clients.Caller.SendAsync(BenchmarkRunHubEvents.ReplayReset,
                new BenchmarkRunReplayReset
                {
                    RunId = runId,
                    LatestSequence = latestSequence,
                    RunVersion = run.Version
                },
                cancellationToken);
            return;
        }

        foreach (var streamEvent in replay.Events)
        {
            await Clients.Caller.SendAsync(BenchmarkRunHubEvents.Event, streamEvent, cancellationToken);
        }
    }

    public Task Unsubscribe(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, RunGroup(runId), Context.ConnectionAborted);
}
