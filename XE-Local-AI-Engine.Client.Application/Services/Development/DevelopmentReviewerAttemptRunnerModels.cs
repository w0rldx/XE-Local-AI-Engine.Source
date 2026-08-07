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

internal sealed record DevelopmentReviewerAttemptResult(
    Guid AttemptId,
    Guid ArtifactId,
    DevelopmentReviewDisposition Disposition,
    DevelopmentTaskStatus TaskStatus,
    string SubjectHash);
