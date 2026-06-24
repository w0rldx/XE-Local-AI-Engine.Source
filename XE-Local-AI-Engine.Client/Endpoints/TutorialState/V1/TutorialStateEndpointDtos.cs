namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1;

using XE_Local_AI_Engine.Client.Services.Tutorial;

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

internal static class TutorialStateMapper
{
    public const string StatusCompleted = "completed";
    public const string StatusSkipped = "skipped";

    public static TutorialStateResponse ToResponse(this IReadOnlyList<TutorialStateEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new TutorialStateResponse
        {
            Entries =
            [
                .. entries.Select(static entry => new TutorialStateEntryResponse
                {
                    Key = entry.Key,
                    Status = ToWireStatus(entry.Status),
                    AtUtc = entry.AtUtc
                })
            ]
        };
    }

    public static bool TryParseStatus(string? value, out TutorialStatus status)
    {
        switch (value)
        {
            case StatusCompleted:
                status = TutorialStatus.Completed;
                return true;
            case StatusSkipped:
                status = TutorialStatus.Skipped;
                return true;
            default:
                status = TutorialStatus.Skipped;
                return false;
        }
    }

    private static string ToWireStatus(TutorialStatus status)
    {
        return status switch
        {
            TutorialStatus.Completed => StatusCompleted,
            TutorialStatus.Skipped => StatusSkipped,
            _ => StatusSkipped
        };
    }
}
