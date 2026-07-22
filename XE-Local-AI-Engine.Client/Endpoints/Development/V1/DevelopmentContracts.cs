namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class CreateDevelopmentProjectRequest
{
    public Guid OperationId { get; init; }
    public Guid SelectedFolderId { get; init; }
    public string Objective { get; init; } = string.Empty;
    public string BaseBranch { get; init; } = "main";
    public string TaskTitle { get; init; } = string.Empty;
    public string Requirements { get; init; } = string.Empty;
    public string AcceptanceCriteriaJson { get; init; } = "[]";
    public string EgressPolicy { get; init; } = nameof(DevelopmentEgressPolicy.LocalOnly);
    public string CoderModelId { get; init; } = string.Empty;
    public string ReviewerModelId { get; init; } = string.Empty;
    public bool TrustedRepositoryAcknowledged { get; init; }
    public int? MaxTokens { get; init; }
    public int? MaxDurationSeconds { get; init; }
}

public sealed class DevelopmentProjectRequest
{
    public Guid ProjectId { get; init; }
}

public sealed class DevelopmentTaskRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
}

public sealed class DevelopmentAttemptRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid AttemptId { get; init; }
}

public sealed class DevelopmentArtifactRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid ArtifactId { get; init; }
}

public sealed class DevelopmentActionRequest
{
    public Guid ProjectId { get; init; }
    public Guid TaskId { get; init; }
    public Guid OperationId { get; init; }
}

public sealed class RegisterDevelopmentRepositoryRequest
{
    public string Alias { get; init; } = string.Empty;
    public string HostPath { get; init; } = string.Empty;
}

public sealed class ReconnectDevelopmentRepositoryRequest
{
    public Guid ProjectId { get; init; }
    public Guid SelectedFolderId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed record DevelopmentCapabilityResponse(bool Enabled);

public sealed record DevelopmentRepositoryResponse(string Id, string Alias, string Availability);

public sealed record DevelopmentProjectResponse(
    Guid Id,
    string Objective,
    Guid? SelectedFolderId,
    bool RepositoryConnectionRequired,
    string BaseBranch,
    string Status,
    string EgressPolicy,
    string? CoderModelId,
    string? ReviewerModelId,
    int? MaxTokens,
    int? MaxDurationSeconds,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

public sealed record DevelopmentTaskResponse(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    string Status,
    int CurrentReviewRound,
    int MaxReviewRounds,
    string? BlockedReason,
    string? ApprovedSubjectHash,
    long Version);

public sealed record DevelopmentAttemptResponse(
    Guid Id,
    Guid TaskId,
    Guid? PredecessorAttemptId,
    string Role,
    string ModelId,
    string Provider,
    string Status,
    long? StartedAtUtc,
    long? EndedAtUtc,
    string? TerminalReason,
    long? InputTokens,
    long? OutputTokens,
    long Version);

public sealed record DevelopmentArtifactResponse(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    string Kind,
    string ContentHash,
    long ByteCount,
    long CreatedAtUtc,
    string? BaseCommit,
    string? SubjectHash,
    string? ChangedFilesManifestHash,
    string? CommandProfileVersion,
    bool IsValid);

public sealed record DevelopmentEventResponse(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    long Sequence,
    string EventType,
    long OccurredAtUtc,
    Guid? OperationId,
    string? OperationPhase,
    string? Outcome);

public sealed record DevelopmentTaskDetailResponse(
    DevelopmentTaskResponse Task,
    IReadOnlyList<DevelopmentAttemptResponse> Attempts,
    IReadOnlyList<DevelopmentArtifactResponse> Artifacts);

public sealed record DevelopmentProjectDetailResponse(
    DevelopmentProjectResponse Project,
    IReadOnlyList<DevelopmentTaskDetailResponse> Tasks,
    IReadOnlyList<DevelopmentEventResponse> Events);

public sealed record ListDevelopmentProjectsResponse(IReadOnlyList<DevelopmentProjectResponse> Items);
public sealed record ListDevelopmentRepositoriesResponse(IReadOnlyList<DevelopmentRepositoryResponse> Items);
public sealed record ListDevelopmentEventsResponse(IReadOnlyList<DevelopmentEventResponse> Items);
public sealed record ListDevelopmentArtifactsResponse(IReadOnlyList<DevelopmentArtifactResponse> Items);
public sealed record DevelopmentArtifactContentResponse(DevelopmentArtifactResponse Artifact, string Content);
public sealed record DevelopmentNextActionResponse(string Action, Guid ProjectId, Guid TaskId, Guid? AttemptId, string TaskStatus, string? Role);
public sealed record DevelopmentPatchPreviewResponse(string SubjectHash,
    string PatchHash,
    string ManifestHash,
    string ExpectedResultHash,
    string Patch,
    IReadOnlyList<DevelopmentPatchPreviewFile> ChangedFiles);
public sealed record DevelopmentApplyResponse(Guid OperationId, string Phase, string Outcome, string Status, long Version, long Sequence);

internal static class DevelopmentContractMapper
{
    public static DevelopmentProjectResponse ToResponse(this DevelopmentProjectSnapshot value)
        => new(value.Id,
            value.Objective,
            value.SelectedFolderId,
            value.SelectedFolderId is null,
            value.BaseBranch,
            value.Status.ToString(),
            value.EgressPolicy.ToString(),
            value.CoderModelId,
            value.ReviewerModelId,
            value.MaxTokens,
            value.MaxDurationSeconds,
            value.CreatedAtUtc,
            value.UpdatedAtUtc,
            value.Version);

    public static DevelopmentRepositoryResponse ToResponse(this DevelopmentRepositoryReference value)
        => new(value.Id, value.Alias, value.Availability);

    public static DevelopmentTaskResponse ToResponse(this DevelopmentTaskSnapshot value)
        => new(value.Id,
            value.ProjectId,
            value.Title,
            value.Requirements,
            value.AcceptanceCriteriaJson,
            value.Status.ToString(),
            value.CurrentReviewRound,
            value.MaxReviewRounds,
            value.BlockedReason,
            value.ApprovedSubjectHash,
            value.Version);

    public static DevelopmentAttemptResponse ToResponse(this DevelopmentAttemptSnapshot value)
        => new(value.Id,
            value.TaskId,
            value.PredecessorAttemptId,
            value.Role.ToString(),
            value.ModelId,
            value.Provider,
            value.Status.ToString(),
            value.StartedAtUtc,
            value.EndedAtUtc,
            value.TerminalReason,
            value.InputTokens,
            value.OutputTokens,
            value.Version);

    public static DevelopmentArtifactResponse ToResponse(this DevelopmentArtifactSnapshot value)
        => new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Kind.ToString(),
            value.ContentHash,
            value.ByteCount,
            value.CreatedAtUtc,
            value.BaseCommit,
            value.SubjectHash,
            value.ChangedFilesManifestHash,
            value.CommandProfileVersion,
            value.IsValid);

    public static DevelopmentEventResponse ToResponse(this DevelopmentEventSnapshot value)
        => new(value.Id,
            value.ProjectId,
            value.TaskId,
            value.AttemptId,
            value.Sequence,
            value.EventType,
            value.OccurredAtUtc,
            value.OperationId,
            value.OperationPhase,
            value.Outcome);

    public static DevelopmentTaskDetailResponse ToResponse(this DevelopmentTaskAggregate value)
        => new(value.Task.ToResponse(),
            value.Attempts.Select(ToResponse).ToArray(),
            value.Artifacts.Select(ToResponse).ToArray());

    public static DevelopmentProjectDetailResponse ToResponse(this DevelopmentProjectAggregate value)
        => new(value.Project.ToResponse(),
            value.Tasks.Select(ToResponse).ToArray(),
            value.Events.Select(ToResponse).ToArray());
}
