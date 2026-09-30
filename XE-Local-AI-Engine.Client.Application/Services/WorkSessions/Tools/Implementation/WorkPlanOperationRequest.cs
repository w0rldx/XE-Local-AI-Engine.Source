namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

/// <summary>
///     One plan operation as the model wrote it.
/// </summary>
/// <remarks>
///     <c>name</c>, <c>text</c> and <c>summary</c> are aliases for <c>title</c>, because a small model reliably
///     reaches for one of them instead and an unknown key fails the WHOLE batch at deserialization, after which the
///     retry spends the step's entire provider-call budget guessing.
/// </remarks>
internal sealed record WorkPlanOperationRequest(
    string? Op,
    string? TaskId,
    string? Title,
    string? Name,
    string? Text,
    string? Summary,
    string? Detail,
    string? Status,
    string? BlockedReason,
    string? ParentTaskId)
{
    /// <summary>The title under whichever key it arrived, trimmed; null when none of them carried anything.</summary>
    public string? EffectiveTitle => Trimmed(Title) ?? Trimmed(Name) ?? Trimmed(Text) ?? Trimmed(Summary);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
