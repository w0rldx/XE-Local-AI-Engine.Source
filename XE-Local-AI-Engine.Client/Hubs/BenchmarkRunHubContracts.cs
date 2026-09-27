namespace XE_Local_AI_Engine.Client.Hubs;

public static class BenchmarkRunHubEvents
{
    public const string Event = "benchmarkRun.event";
    public const string ReplayReset = "benchmarkRun.replayReset";
}

public sealed class BenchmarkRunReplayReset
{
    public required Guid RunId { get; init; }

    public required long LatestSequence { get; init; }

    public required long RunVersion { get; init; }
}
