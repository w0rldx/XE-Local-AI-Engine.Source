namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>A live session stopped admitting audio and is draining; the browser should stop capturing.</summary>
public sealed class TranscriptionAdmissionClosedPush
{
    public required Guid SessionId { get; init; }
}
