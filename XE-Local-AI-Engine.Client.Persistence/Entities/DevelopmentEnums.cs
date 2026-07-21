namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum DevelopmentProjectStatus
{
    Active,
    Completed,
    Blocked,
    Cancelled
}

public enum DevelopmentEgressMode
{
    LocalOnly,
    CloudScoped
}

public enum DevelopmentTaskStatus
{
    Planned,
    Ready,
    InProgress,
    Validation,
    InReview,
    ChangesRequested,
    AwaitingApply,
    Completed,
    Blocked,
    Cancelled
}

public enum DevelopmentAttemptRole
{
    Coder,
    Reviewer
}

public enum DevelopmentAttemptStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Interrupted,
    Cancelled
}

public enum DevelopmentArtifactKind
{
    WorkspaceManifest,
    CloudContextBundle,
    Patch,
    ChangedFilesManifest,
    CommandResult,
    ValidationReport,
    ReviewReport
}
