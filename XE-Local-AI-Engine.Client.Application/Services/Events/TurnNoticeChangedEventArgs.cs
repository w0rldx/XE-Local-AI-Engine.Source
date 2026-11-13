namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for turn-notice changed notifications.
/// </summary>
public sealed class TurnNoticeChangedEventArgs : EventArgs
{
    public TurnNoticeChangedEventArgs(TurnNoticePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Payload = payload;
    }

    public TurnNoticePayload Payload { get; }
}
