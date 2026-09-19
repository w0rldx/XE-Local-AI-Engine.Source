namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record DevelopmentReviewReport(
    DevelopmentReviewDisposition Disposition,
    string Summary,
    IReadOnlyList<DevelopmentReviewFinding> Findings,
    int ReviewRound,
    string BaseCommit,
    string SubjectHash,
    string ManifestHash,
    string ExpectedResultHash,
    Guid ValidationArtifactId,
    long CompletedAtUtc);

internal sealed class DevelopmentReviewerAttemptResult
{
    public required Guid AttemptId { get; init; }

    public required Guid ArtifactId { get; init; }

    public required DevelopmentReviewDisposition Disposition { get; init; }

    public required DevelopmentTaskStatus TaskStatus { get; init; }

    public required string SubjectHash { get; init; }
}
