namespace XE_Local_AI_Engine.Client.Models.Events;

public sealed record InvocationAssignedEvent
{
    public required RuntimePackage RuntimePackage { get; init; }
}
