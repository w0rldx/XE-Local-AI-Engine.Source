namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

public static class WorkSessionHubEvents
{
    public const string Changed = "workSessionChanged";
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
