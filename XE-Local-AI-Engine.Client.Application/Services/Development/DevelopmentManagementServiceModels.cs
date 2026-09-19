namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class DevelopmentCreateProjectInput
{
    public required Guid OperationId { get; init; }

    public required Guid SelectedFolderId { get; init; }

    public required string Objective { get; init; }

    public required string BaseBranch { get; init; }

    public required string TaskTitle { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public required DevelopmentEgressPolicy EgressPolicy { get; init; }

    public required string CoderModelId { get; init; }

    public required string ReviewerModelId { get; init; }

    public required bool TrustedRepositoryAcknowledged { get; init; }

    public int? MaxTokens { get; init; }

    public int? MaxDurationSeconds { get; init; }

    public string? CommandProfileId { get; init; }

    public string? BuildTarget { get; init; }
}

public sealed class DevelopmentProjectAggregate
{
    public required DevelopmentProjectSnapshot Project { get; init; }

    public required IReadOnlyList<DevelopmentTaskAggregate> Tasks { get; init; }

    public required IReadOnlyList<DevelopmentEventSnapshot> Events { get; init; }
}

/// <summary>
///     <see cref="WorkflowRunId" /> names the development workflow run driving this task, and is null for a task an
///     operator drives themselves. It is the one thing on this aggregate that is not the task's own row: apply is
///     approved at that run's gate, so the page has to know it is not the one being asked.
/// </summary>
public sealed record DevelopmentTaskAggregate
{
    public required DevelopmentTaskSnapshot Task { get; init; }

    public required IReadOnlyList<DevelopmentAttemptSnapshot> Attempts { get; init; }

    public required IReadOnlyList<DevelopmentArtifactSnapshot> Artifacts { get; init; }

    public Guid? WorkflowRunId { get; init; }
}

public sealed class DevelopmentNextActionResult
{
    public required string Action { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required DevelopmentTaskStatus TaskStatus { get; init; }

    public required DevelopmentAttemptRole? Role { get; init; }
}

public sealed class DevelopmentArtifactContent
{
    public required DevelopmentArtifactSnapshot Artifact { get; init; }

    public required string Content { get; init; }
}

public sealed class DevelopmentPatchPreviewResult
{
    public required string SubjectHash { get; init; }

    public required string PatchHash { get; init; }

    public required string ManifestHash { get; init; }

    public required string ExpectedResultHash { get; init; }

    public required string Patch { get; init; }

    public required IReadOnlyList<DevelopmentPatchPreviewFile> ChangedFiles { get; init; }
}

public sealed class DevelopmentPatchPreviewFile
{
    public required string Path { get; init; }

    public required string ChangeType { get; init; }

    public required string? PreviousPath { get; init; }
}
