namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     One in-host capture lane has had no audio for <see cref="QuietMs" />, or hears audio again when it is <c>0</c>.
///     Non-terminal: a paused video and a closed tab look the same to WASAPI, so a quiet source never ends a session.
/// </summary>
public sealed class TranscriptionSourceQuietPush
{
    public required Guid SessionId { get; init; }

    public required string Channel { get; init; }

    public required long QuietMs { get; init; }
}
