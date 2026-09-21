namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum DevelopmentProjectStatus
{
    Active,
    Completed,
    Cancelled
}

public enum DevelopmentEgressPolicy
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
    ReviewReport,
    CoderSubmission,

    /// <summary>
    ///     The sanitized text a coder or reviewer attempt was actually GIVEN, written before the model call so a
    ///     cancelled, timed-out or evidence-rejected attempt still leaves one. Appended last, but the column is a
    ///     string, not an ordinal.
    /// </summary>
    /// <remarks>
    ///     Every other kind records what a model produced or what the gate observed, so without it no system record
    ///     says what the model was told. <c>DevelopmentArtifactConfiguration</c> declares <c>HasConversion&lt;string&gt;()</c>,
    ///     so appending needs no migration and reordering would not corrupt rows. This kind has TWO shapes under one
    ///     name: a coder prompt carries a base commit and no subject (it does not exist yet), a reviewer prompt the
    ///     full subject stamps and its artifacts — the same stamps approval evidence carries, so a read MUST filter by kind and take the role from its attempt.
    /// </remarks>
    Prompt
}
