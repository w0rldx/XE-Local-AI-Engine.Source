namespace XE_Local_AI_Engine.Client.Endpoints.NodeBinding.V1;

/// <summary>
///     Response DTO for node binding session operations.
/// </summary>
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

/// <summary>
///     Request DTO for poll node binding session operations.
/// </summary>
public sealed record PollNodeBindingSessionRequest
{
    public required string DeviceCode { get; init; }

    public required string UserCode { get; init; }

    public required string VerificationUri { get; init; }

    public required string VerificationUriComplete { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public int IntervalSeconds { get; init; }
}

/// <summary>
///     Response DTO for poll node binding session operations.
/// </summary>
public sealed record PollNodeBindingSessionResponse
{
    public required string Status { get; init; }

    public int IntervalSeconds { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
///     Response DTO for cancel node binding operations.
/// </summary>
public sealed record CancelNodeBindingResponse
{
    public bool Cancelled { get; init; }
}
