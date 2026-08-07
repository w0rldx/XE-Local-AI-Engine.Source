namespace XE_Local_AI_Engine.Client.Endpoints.TutorialState.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Tutorial;

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
