namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Request DTO for start node binding operations.
/// </summary>
public sealed record StartNodeBindingRequest
{
    public required string NodeName { get; init; }

    public string? Description { get; init; }

    public string? LocalMachineId { get; init; }
}
