namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

public static class ExternalAppHubEvents
{
    public const string Changed = "externalAppChanged";

    public const string PullProgress = "externalAppPullProgress";
}

/// <summary>
///     A content-free ping: what happened, where the instance now stands, and the sequence it was minted at.
/// </summary>
/// <remarks>
///     The subscriber re-reads the feed from its own watermark, so a dropped push degrades to a late read rather than
///     to a wrong render — and nothing an instance's variables could reach ever rides on the hub.
/// </remarks>
public sealed class ExternalAppChanged
{
    public required Guid InstanceId { get; init; }

    public required long Sequence { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }
}

/// <summary>
///     Image-pull progress for one service. Hub-only: high-frequency and worthless after the fact, so it allocates no
///     sequence and appends no event row.
/// </summary>
public sealed class ExternalAppPullProgress
{
    public required Guid InstanceId { get; init; }

    public required string Service { get; init; }

    public required int LayerCount { get; init; }

    public required int CompletedLayers { get; init; }

    public required long Bytes { get; init; }
}

public sealed class ExternalAppSubscriptionSnapshot
{
    public required Guid InstanceId { get; init; }

    public required string Status { get; init; }

    public required string DesiredState { get; init; }

    public required string? FailureCategory { get; init; }

    public required long LastSequence { get; init; }

    public required IReadOnlyList<ExternalAppInstanceEventView> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}
