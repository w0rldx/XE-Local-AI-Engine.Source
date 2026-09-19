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

public sealed class DevelopmentProgressWarning
{
    public required DevelopmentProgressWarningCategory Category { get; init; }

    public required string Fingerprint { get; init; }

    public required int Count { get; init; }

    public required long OccurredAtUtc { get; init; }

    public required string Message { get; init; }
}
