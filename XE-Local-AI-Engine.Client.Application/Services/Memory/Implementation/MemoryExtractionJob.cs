namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

/// <summary>A queued extraction job: the metadata-only exec-log telemetry plus the content-bearing run input.</summary>
internal sealed class MemoryExtractionJob
{
    public required MemoryExtractionDispatchContext Telemetry { get; init; }

    public required MemoryExtractionRunInput Run { get; init; }
}
