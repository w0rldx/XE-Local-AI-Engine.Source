namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

/// <summary>
///     Current user's recorded onboarding tour entries.
/// </summary>
public sealed record TutorialStateResponse
{
    public required IReadOnlyList<TutorialStateEntryResponse> Entries { get; init; }
}

/// <summary>
///     A single tour outcome on the wire.
/// </summary>
public sealed record TutorialStateEntryResponse
{
    public required string Key { get; init; }

    public required string Status { get; init; }

    public required DateTime AtUtc { get; init; }
}

/// <summary>
///     Upsert one tour entry by key. Status must be "completed" or "skipped".
/// </summary>
public sealed record SaveTutorialStateRequest
{
    public string Key { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

/// <summary>
///     Whether the current user acknowledged the AI responsibility notice, and when (null until they do).
/// </summary>
public sealed record AiAcknowledgementResponse
{
    public required bool Acknowledged { get; init; }

    public DateTime? AcknowledgedAtUtc { get; init; }
}
