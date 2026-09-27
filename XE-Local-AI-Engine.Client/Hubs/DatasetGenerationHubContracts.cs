namespace XE_Local_AI_Engine.Client.Hubs;

public static class DatasetGenerationHubEvents
{
    public const string Event = "datasetGeneration.event";
    public const string ReplayReset = "datasetGeneration.replayReset";
}

public sealed class DatasetGenerationReplayReset
{
    public required Guid DatasetId { get; init; }

    public required long LatestSequence { get; init; }

    public required long DatasetVersion { get; init; }
}
