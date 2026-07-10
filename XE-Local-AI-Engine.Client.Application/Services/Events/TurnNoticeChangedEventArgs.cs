namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for turn-notice changed notifications.
/// </summary>
public sealed class TurnNoticeChangedEventArgs : EventArgs
{
    public TurnNoticeChangedEventArgs(TurnNoticePayload payload)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public TurnNoticePayload Payload { get; }
}
