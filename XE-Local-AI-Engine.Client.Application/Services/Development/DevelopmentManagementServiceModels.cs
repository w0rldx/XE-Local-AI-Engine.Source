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

public sealed record DevelopmentTaskAggregate(
    DevelopmentTaskSnapshot Task,
    IReadOnlyList<DevelopmentAttemptSnapshot> Attempts,
    IReadOnlyList<DevelopmentArtifactSnapshot> Artifacts);

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
