namespace XE_Local_AI_Engine.Client.Models.NodeBinding;

public sealed record StartNodeBindingResponse
{
    public required string DeviceCode { get; init; }

    public required string UserCode { get; init; }

    public required string VerificationUri { get; init; }

    public required string VerificationUriComplete { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public int IntervalSeconds { get; init; }
}
