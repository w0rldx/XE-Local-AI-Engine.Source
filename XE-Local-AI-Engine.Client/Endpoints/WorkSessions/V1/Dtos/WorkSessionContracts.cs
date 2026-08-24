namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class WorkSessionRequest
{
    public Guid SessionId { get; init; }
}

public sealed class WorkSessionArtifactRequest
{
    public Guid SessionId { get; init; }

    public Guid ArtifactId { get; init; }
}

/// <summary>
///     The four unpaged feeds. <c>SinceSeq</c> is an EXCLUSIVE lower bound, so a client that stores the sequence it
///     last rendered replays nothing it already has; 0 asks for everything.
/// </summary>
public sealed class WorkSessionFeedRequest
{
    public Guid SessionId { get; init; }

    public long SinceSeq { get; init; }
}

/// <summary>
///     The event feed. It is the one feed that grows without bound — tasks, findings, artifacts and checkpoints are
///     re-stamped or few — so it is the one that pages.
/// </summary>
public sealed class WorkSessionEventFeedRequest
{
    public Guid SessionId { get; init; }

    public long SinceSeq { get; init; }

    public int Limit { get; init; } = 200;
}

public sealed class CreateWorkSessionRequest
{
    public string Title { get; init; } = string.Empty;

    public string Objective { get; init; } = string.Empty;

    public string Kind { get; init; } = nameof(AgentWorkSessionKind.General);

    public Guid AgentDefinitionId { get; init; }
}

/// <summary>A PATCH body: a null member leaves the stored value alone, it does not clear it.</summary>
public sealed class UpdateWorkSessionRequest
{
    public Guid SessionId { get; init; }

    public string? Title { get; init; }

    public string? Objective { get; init; }

    public Guid? AgentDefinitionId { get; init; }
}

/// <summary>
///     A user follow-up. <see cref="Text" /> carries no length rule here on purpose: the node's message-size cap is
///     the service's, checked before the row is written, and a second copy in a validator would drift from it.
/// </summary>
public sealed class PostWorkSessionMessageRequest
{
    public Guid SessionId { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>
///     One work session in full.
///     <para>
///         <see cref="Version" /> is the optimistic-concurrency token behind the 409s, <see cref="MaxStepsPerRun" />
///         the denominator of the "step N of M" the session view renders, and <see cref="LastSequence" /> the
///         watermark a hub subscriber replays from.
///     </para>
/// </summary>
public sealed record WorkSessionResponse(Guid Id,
    string Title,
    string Objective,
    string Kind,
    Guid AgentDefinitionId,
    Guid ConversationId,
    string Status,
    Guid? CurrentTaskId,
    int StepCount,
    int MaxStepsPerRun,
    Guid? LastCheckpointId,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version,
    long LastSequence);

/// <summary>
///     One row of the session list. Deliberately not <see cref="WorkSessionResponse" />: the list never renders an
///     objective, and the service's list projection does not read one, so a shared record would have to invent
///     values the node never loaded.
/// </summary>
public sealed record WorkSessionSummaryResponse(Guid Id,
    string Title,
    string Kind,
    string Status,
    Guid AgentDefinitionId,
    int StepCount,
    long UpdatedAtUtc);

public sealed record WorkSessionTaskResponse(Guid Id,
    Guid? ParentTaskId,
    long Sequence,
    string Title,
    string? Detail,
    string Status,
    string? BlockedReason,
    string Origin,
    int CreatedStep,
    int UpdatedStep);

public sealed record WorkSessionFindingResponse(Guid Id,
    Guid? TaskId,
    long Sequence,
    string Kind,
    string Text,
    string? SourceRef,
    int CreatedStep,
    bool Superseded);

/// <summary>
///     An artifact's metadata. There is deliberately no member for the blob path the node stores it under: it is a
///     host path, it is of no use to a client, and a response is the one place it could leak from.
/// </summary>
public sealed record WorkSessionArtifactResponse(Guid Id,
    long Sequence,
    string Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    bool IsValid,
    int CreatedStep);

/// <summary>
///     A checkpoint. <see cref="Summary" /> is null on a node with no local model to summarize with — the structured
///     <see cref="StateJson" /> is the part the resume path actually depends on.
/// </summary>
public sealed record WorkSessionCheckpointResponse(Guid Id,
    long Sequence,
    int Step,
    string? Summary,
    string StateJson,
    long CreatedAtUtc);

public sealed record WorkSessionEventResponse(Guid Id,
    long Sequence,
    int Step,
    string EventType,
    string? DetailJson,
    string? Outcome,
    long OccurredAtUtc);

/// <summary>
///     An artifact with its bytes. <see cref="IsBase64" /> is decided from the media type, never by sniffing the
///     bytes, so binary content is never handed over as mangled UTF-8.
/// </summary>
public sealed record WorkSessionArtifactContentResponse(WorkSessionArtifactResponse Artifact, string Content, bool IsBase64);

public sealed record PostWorkSessionMessageResponse(Guid MessageId, Guid ConversationId);

public sealed record ListWorkSessionsResponse(IReadOnlyList<WorkSessionSummaryResponse> Items);

// One concrete response record per feed rather than one generic envelope: NSwag builds schema ids from the CLR type
// name, and a generic would land in the generated client as an unreadable ListWorkSessionFeedResponseOfT.
public sealed record ListWorkSessionTasksResponse(IReadOnlyList<WorkSessionTaskResponse> Items, long LastSequence);

public sealed record ListWorkSessionFindingsResponse(IReadOnlyList<WorkSessionFindingResponse> Items, long LastSequence);

public sealed record ListWorkSessionArtifactsResponse(IReadOnlyList<WorkSessionArtifactResponse> Items, long LastSequence);

public sealed record ListWorkSessionCheckpointsResponse(IReadOnlyList<WorkSessionCheckpointResponse> Items, long LastSequence);

/// <summary>
///     A page of events. <see cref="HasMore" /> rides only this feed because it is the only paged one; the client
///     follows it by re-reading from <see cref="LastSequence" />.
/// </summary>
public sealed record ListWorkSessionEventsResponse(IReadOnlyList<WorkSessionEventResponse> Items, long LastSequence, bool HasMore);
