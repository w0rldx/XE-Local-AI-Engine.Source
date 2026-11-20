namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

public static class WorkSessionHubEvents
{
    public const string Changed = "workSessionChanged";

    /// <summary>Broadcast to every connection beside each <c>status</c> change, for the operations feed, which watches no session.</summary>
    public const string StatusChanged = "workSessionStatusChanged";
}

/// <summary>
///     A session's status moved. Only the id: the publish seam carries no status, and the feed re-reads the session list,
///     which is the authority either way.
/// </summary>
public sealed class WorkSessionStatusChanged
{
    public required Guid SessionId { get; init; }
}

/// <summary>
///     What changed and where the store now stands.
/// </summary>
/// <remarks>
///     <see cref="Kind" /> is lowercase on the wire — the client switches on the literal. The payload deliberately
///     carries no content: the subscriber re-reads the named feed from its own watermark, so a dropped push degrades
///     to a late read rather than to a wrong render.
/// </remarks>
public sealed class WorkSessionChanged
{
    public required Guid SessionId { get; init; }

    public required long Seq { get; init; }

    public required string Kind { get; init; }
}

public sealed class WorkSessionSubscriptionSnapshot
{
    public required Guid SessionId { get; init; }

    public required string Status { get; init; }

    public required int Step { get; init; }

    public required Guid? CurrentTaskId { get; init; }

    public required long LastSeq { get; init; }

    public required IReadOnlyList<WorkSessionEventResponse> Events { get; init; }

    public required bool ReplayTruncated { get; init; }
}
