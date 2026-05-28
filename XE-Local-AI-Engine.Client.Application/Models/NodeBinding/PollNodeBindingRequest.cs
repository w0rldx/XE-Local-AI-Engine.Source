namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

public sealed record PollNodeBindingRequest
{
    public required string DeviceCode { get; init; }
}
