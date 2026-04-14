namespace XE_Local_AI_Engine.Client.Models.Events;

public sealed record DisconnectRequestedEvent
{
    public required string Reason { get; init; }
}