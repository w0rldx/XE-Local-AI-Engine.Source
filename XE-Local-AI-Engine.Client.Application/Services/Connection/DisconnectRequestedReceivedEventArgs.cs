namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Event payload for disconnect requested received notifications.
/// </summary>
public sealed class DisconnectRequestedReceivedEventArgs : EventArgs
{
    public DisconnectRequestedReceivedEventArgs(DisconnectRequestedEvent disconnectRequest)
    {
        DisconnectRequest = disconnectRequest ?? throw new ArgumentNullException(nameof(disconnectRequest));
    }

    public DisconnectRequestedEvent DisconnectRequest { get; }
}
