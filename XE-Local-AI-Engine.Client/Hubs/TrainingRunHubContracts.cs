namespace XE_Local_AI_Engine.Client.Hubs;

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
