namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

public sealed record StartNodeBindingRequest
{
    public required string NodeName { get; init; }

    public string? Description { get; init; }

    public string? LocalMachineId { get; init; }
}
