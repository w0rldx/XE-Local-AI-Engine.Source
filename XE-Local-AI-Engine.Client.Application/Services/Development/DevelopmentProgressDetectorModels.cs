namespace XE_Local_AI_Engine.Client.Services.Development;

public enum DevelopmentProgressWarningCategory
{
    RepeatedTool,
    RepeatedCommandFailure,
    NoMeaningfulProgress,
    SubjectOscillation,
    ProviderRoundLimit,
    ToolCallLimit,
    ContextHeadroom,
    RepeatedReviewFinding,
    PlanningWithoutArtifactProgress
}

public enum DevelopmentMeaningfulProgressKind
{
    Artifact,
    File,
    Validation,
    ReviewFinding
}

public sealed record DevelopmentProgressWarning(
    DevelopmentProgressWarningCategory Category,
    string Fingerprint,
    int Count,
    long OccurredAtUtc,
    string Message);
