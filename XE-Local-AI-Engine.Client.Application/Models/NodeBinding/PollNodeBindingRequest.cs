namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Request DTO for poll node binding operations.
/// </summary>
public sealed record PollNodeBindingRequest
{
    public required string DeviceCode { get; init; }
}
