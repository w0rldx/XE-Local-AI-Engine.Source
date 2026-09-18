namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public static class DatasetGenerationHubEvents
{
    public const string Event = "datasetGeneration.event";
    public const string ReplayReset = "datasetGeneration.replayReset";
}

public sealed record DatasetGenerationReplayReset(Guid DatasetId, long LatestSequence, long DatasetVersion);

/// <summary>
///     Operator-only, per-dataset delivery for live generation progress. The caller joins the group before replay so the
///     subscribe-after-publish race closes; the overlap is deduplicated client-side by event sequence.
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class DatasetGenerationHub : Hub
{
    private readonly TrainingDatasetService _datasets;
    private readonly IDatasetGenerationEventBuffer _events;

    public DatasetGenerationHub(TrainingDatasetService datasets, IDatasetGenerationEventBuffer events)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(events);
        _datasets = datasets;
        _events = events;
    }

    public static string DatasetGroup(Guid datasetId) =>
        $"dataset-generation-{datasetId:N}";

    public async Task Subscribe(Guid datasetId, long afterSeq)
    {
        if (datasetId == Guid.Empty)
        {
            throw new HubException("The dataset id is required.");
        }

        if (afterSeq < -1)
        {
            throw new HubException("The replay sequence is invalid.");
        }

        var cancellationToken = Context.ConnectionAborted;
        var dataset = await _datasets.GetAsync(datasetId, cancellationToken)
                      ?? throw new HubException("The training dataset was not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, DatasetGroup(datasetId), cancellationToken);

        var replay = _events.Replay(datasetId, afterSeq);
        if (replay.ResetRequired)
        {
            await Clients.Caller.SendAsync(DatasetGenerationHubEvents.ReplayReset,
                             new DatasetGenerationReplayReset(datasetId, replay.LatestSequence, dataset.Version),
                             cancellationToken);
            return;
        }

        foreach (var generationEvent in replay.Events)
        {
            await Clients.Caller.SendAsync(DatasetGenerationHubEvents.Event, generationEvent, cancellationToken);
        }
    }

    public Task Unsubscribe(Guid datasetId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DatasetGroup(datasetId), Context.ConnectionAborted);
}
