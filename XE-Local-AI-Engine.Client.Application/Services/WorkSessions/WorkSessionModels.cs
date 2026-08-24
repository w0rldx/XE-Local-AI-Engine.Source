namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>One row of the work-session list. Deliberately carries no objective — the list never renders one.</summary>
public sealed record WorkSessionSummary(Guid Id,
    string Title,
    AgentWorkSessionKind Kind,
    AgentWorkSessionStatus Status,
    Guid AgentDefinitionId,
    int StepCount,
    long UpdatedUtc);

/// <summary>
///     One work session in full.
///     <para>
///         <see cref="MaxStepsPerRun" /> is the node's EFFECTIVE option value rather than a stored column, so the
///         session view can render "step N of M" without a second settings round-trip. <see cref="LastSequence" /> is
///         the hub watermark a subscriber replays from; <see cref="Version" /> is the optimistic-concurrency token a
///         later update or lifecycle call echoes back.
///     </para>
/// </summary>
public sealed record WorkSessionDetail(Guid Id,
    string Title,
    string Objective,
    AgentWorkSessionKind Kind,
    AgentWorkSessionStatus Status,
    Guid AgentDefinitionId,
    Guid ConversationId,
    Guid? CurrentTaskId,
    int StepCount,
    int MaxStepsPerRun,
    Guid? LastCheckpointId,
    long LastSequence,
    long Version,
    long CreatedUtc,
    long UpdatedUtc);

public sealed record WorkSessionTaskDto(Guid Id,
    Guid? ParentTaskId,
    long Sequence,
    string Title,
    string? Detail,
    AgentWorkSessionTaskStatus Status,
    string? BlockedReason,
    AgentWorkSessionTaskOrigin Origin,
    int CreatedStep,
    int UpdatedStep);

public sealed record WorkSessionFindingDto(Guid Id,
    Guid? TaskId,
    long Sequence,
    AgentWorkSessionFindingKind Kind,
    string Text,
    string? SourceRef,
    int CreatedStep,
    bool Superseded);

public sealed record WorkSessionArtifactDto(Guid Id,
    long Sequence,
    AgentWorkSessionArtifactKind Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    bool IsValid,
    int CreatedStep);

public sealed record WorkSessionCheckpointDto(Guid Id,
    long Sequence,
    int Step,
    string? Summary,
    string StateJson,
    long CreatedUtc);

public sealed record WorkSessionEventDto(Guid Id,
    long Sequence,
    int Step,
    string EventType,
    string? DetailJson,
    string? Outcome,
    long OccurredUtc);

/// <summary>
///     An artifact's bytes as text. <see cref="IsBase64" /> is set for a media type the node cannot hand over as UTF-8,
///     so a caller never has to guess whether the payload is decodable.
/// </summary>
public sealed record WorkSessionArtifactContent(WorkSessionArtifactDto Artifact, string Content, bool IsBase64);

/// <summary>
///     Create input. Named <c>…RequestModel</c> so it cannot collide with the store layer's
///     <c>CreateWorkSessionCommand</c>, which carries the ids and the concurrency token this one has no business
///     knowing about.
/// </summary>
public sealed record CreateWorkSessionRequestModel(string Title, string Objective, AgentWorkSessionKind Kind, Guid AgentDefinitionId);

/// <summary>Update input. A null member leaves the stored value alone.</summary>
public sealed record UpdateWorkSessionRequestModel(string? Title, string? Objective, Guid? AgentDefinitionId);
