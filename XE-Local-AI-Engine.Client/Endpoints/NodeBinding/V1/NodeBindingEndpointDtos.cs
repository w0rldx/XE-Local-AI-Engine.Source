namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

public sealed record NodeBindingSessionResponse
{
    public required string DeviceCode { get; init; }

    public required string UserCode { get; init; }

    public required string VerificationUri { get; init; }

    public required string VerificationUriComplete { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public int IntervalSeconds { get; init; }

    public required string Status { get; init; }
}

public sealed record PollNodeBindingSessionRequest
{
    public required string DeviceCode { get; init; }

    public required string UserCode { get; init; }

    public required string VerificationUri { get; init; }

    public required string VerificationUriComplete { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public int IntervalSeconds { get; init; }
}

public sealed record PollNodeBindingSessionResponse
{
    public required string Status { get; init; }

    public int IntervalSeconds { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record CancelNodeBindingResponse
{
    public bool Cancelled { get; init; }
}
