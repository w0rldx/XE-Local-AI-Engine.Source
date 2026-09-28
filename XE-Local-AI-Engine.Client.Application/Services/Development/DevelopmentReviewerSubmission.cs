namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed record DevelopmentReviewerSubmission
{
    public required DevelopmentReviewDisposition Disposition { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<DevelopmentReviewFinding> Findings { get; init; }
}
