namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed record DevelopmentCreateProjectInput(
    Guid OperationId,
    Guid SelectedFolderId,
    string Objective,
    string BaseBranch,
    string TaskTitle,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentEgressPolicy EgressPolicy,
    string CoderModelId,
    string ReviewerModelId,
    bool TrustedRepositoryAcknowledged,
    int? MaxTokens = null,
    int? MaxDurationSeconds = null,
    string? CommandProfileId = null,
    string? BuildTarget = null);

public sealed record DevelopmentProjectAggregate(
    DevelopmentProjectSnapshot Project,
    IReadOnlyList<DevelopmentTaskAggregate> Tasks,
    IReadOnlyList<DevelopmentEventSnapshot> Events);

/// <summary>
///     <see cref="WorkflowRunId" /> names the development workflow run driving this task, and is null for a task an
///     operator drives themselves. It is the one thing on this aggregate that is not the task's own row: apply is
///     approved at that run's gate, so the page has to know it is not the one being asked.
/// </summary>
public sealed record DevelopmentTaskAggregate(
    DevelopmentTaskSnapshot Task,
    IReadOnlyList<DevelopmentAttemptSnapshot> Attempts,
    IReadOnlyList<DevelopmentArtifactSnapshot> Artifacts,
    Guid? WorkflowRunId = null);

public sealed record DevelopmentNextActionResult(
    string Action,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    DevelopmentTaskStatus TaskStatus,
    DevelopmentAttemptRole? Role);

public sealed record DevelopmentArtifactContent(
    DevelopmentArtifactSnapshot Artifact,
    string Content);

public sealed record DevelopmentPatchPreviewResult(
    string SubjectHash,
    string PatchHash,
    string ManifestHash,
    string ExpectedResultHash,
    string Patch,
    IReadOnlyList<DevelopmentPatchPreviewFile> ChangedFiles);

public sealed record DevelopmentPatchPreviewFile(string Path, string ChangeType, string? PreviousPath);
