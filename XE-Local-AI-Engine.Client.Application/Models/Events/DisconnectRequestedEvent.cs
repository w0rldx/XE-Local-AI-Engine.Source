namespace XE_Local_AI_Engine.Client.Models.Events;

/// <summary>
///     Value object carrying disconnect requested event data.
/// </summary>
public sealed record DisconnectRequestedEvent
{
    public required string Reason { get; init; }
}
