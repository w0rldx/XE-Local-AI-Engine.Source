namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>How far behind real time a live session's transcription is: its queued, untranscribed audio.</summary>
public sealed class TranscriptionCatchUpProgressPush
{
    public required Guid SessionId { get; init; }

    public required long BufferedMs { get; init; }
}
