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
    ///     The sanitized text a coder or reviewer attempt was actually GIVEN. Every other kind records what a model
    ///     produced or what the deterministic gate observed, so until this existed no system record said what the
    ///     model had been told: three live passes' worth of claims about prompt content were model-quoted, never
    ///     evidence. Written before the model call, so a cancelled, timed-out or evidence-rejected attempt still
    ///     leaves one.
    ///     <para>
    ///         Appended at the end deliberately, though the column is a string conversion rather than an ordinal
    ///         (<c>DevelopmentArtifactConfiguration</c> declares <c>HasConversion&lt;string&gt;()</c>), so no
    ///         migration is needed and reordering would not corrupt existing rows either.
    ///     </para>
    ///     <para>
    ///         Unlike every other kind this one has TWO shapes under one name: a coder prompt carries a base commit
    ///         and no subject, because the subject does not exist when it is written, while a reviewer prompt carries
    ///         the full subject stamps and the artifacts it was built from — which are the same stamps approval
    ///         evidence carries, leaving the kind as the only field between them. Every existing gate filters on kind
    ///         first, so this is safe today; any future "latest artifact for this subject" query MUST filter by kind,
    ///         and any read of this kind must also discriminate the role via the attempt it hangs off.
    ///     </para>
    /// </summary>
    Prompt
}
