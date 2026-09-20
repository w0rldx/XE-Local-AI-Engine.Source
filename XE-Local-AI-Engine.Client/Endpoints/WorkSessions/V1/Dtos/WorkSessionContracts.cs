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
public sealed class WorkSessionResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Objective { get; init; }

    public required string Kind { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string Status { get; init; }

    public required Guid? CurrentTaskId { get; init; }

    public required int StepCount { get; init; }

    public required int MaxStepsPerRun { get; init; }

    public required Guid? LastCheckpointId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }

    public required long LastSequence { get; init; }
}

/// <summary>
///     One row of the session list. Deliberately not <see cref="WorkSessionResponse" />: the list never renders an
///     objective, and the service's list projection does not read one, so a shared type would have to invent
///     values the node never loaded.
/// </summary>
public sealed class WorkSessionSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required int StepCount { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class WorkSessionTaskResponse
{
    public required Guid Id { get; init; }

    public required Guid? ParentTaskId { get; init; }

    public required long Sequence { get; init; }

    public required string Title { get; init; }

    public required string? Detail { get; init; }

    public required string Status { get; init; }

    public required string? BlockedReason { get; init; }

    public required string Origin { get; init; }

    public required int CreatedStep { get; init; }

    public required int UpdatedStep { get; init; }
}

public sealed class WorkSessionFindingResponse
{
    public required Guid Id { get; init; }

    public required Guid? TaskId { get; init; }

    public required long Sequence { get; init; }

    public required string Kind { get; init; }

    public required string Text { get; init; }

    public required string? SourceRef { get; init; }

    public required int CreatedStep { get; init; }

    public required bool Superseded { get; init; }
}

/// <summary>
///     An artifact's metadata. There is deliberately no member for the blob path the node stores it under: it is a
///     host path, it is of no use to a client, and a response is the one place it could leak from.
/// </summary>
public sealed class WorkSessionArtifactResponse
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required bool IsValid { get; init; }

    public required int CreatedStep { get; init; }
}

/// <summary>
///     A checkpoint. <see cref="Summary" /> is null on a node with no local model to summarize with — the structured
///     <see cref="StateJson" /> is the part the resume path actually depends on.
/// </summary>
public sealed class WorkSessionCheckpointResponse
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string? Summary { get; init; }

    public required string StateJson { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     One journal entry. <see cref="OperationId" /> is the tool call the entry belongs to when it has one, so a client
///     can group a step's rows by the operation that produced them; it is null for entries no single tool call owns.
/// </summary>
public sealed class WorkSessionEventResponse
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string EventType { get; init; }

    public required string? DetailJson { get; init; }

    public required string? Outcome { get; init; }

    public required long OccurredAtUtc { get; init; }

    public required Guid? OperationId { get; init; }
}

/// <summary>
///     An artifact with its bytes. <see cref="IsBase64" /> is decided from the media type, never by sniffing the
///     bytes, so binary content is never handed over as mangled UTF-8.
/// </summary>
public sealed class WorkSessionArtifactContentResponse
{
    public required WorkSessionArtifactResponse Artifact { get; init; }

    public required string Content { get; init; }

    public required bool IsBase64 { get; init; }
}

public sealed class PostWorkSessionMessageResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }
}

public sealed class ListWorkSessionsResponse
{
    public required IReadOnlyList<WorkSessionSummaryResponse> Items { get; init; }
}

// One concrete response type per feed rather than one generic envelope: NSwag builds schema ids from the CLR type
// name, and a generic would land in the generated client as an unreadable ListWorkSessionFeedResponseOfT.
public sealed class ListWorkSessionTasksResponse
{
    public required IReadOnlyList<WorkSessionTaskResponse> Items { get; init; }

    public required long LastSequence { get; init; }
}

public sealed class ListWorkSessionFindingsResponse
{
    public required IReadOnlyList<WorkSessionFindingResponse> Items { get; init; }

    public required long LastSequence { get; init; }
}

public sealed class ListWorkSessionArtifactsResponse
{
    public required IReadOnlyList<WorkSessionArtifactResponse> Items { get; init; }

    public required long LastSequence { get; init; }
}

public sealed class ListWorkSessionCheckpointsResponse
{
    public required IReadOnlyList<WorkSessionCheckpointResponse> Items { get; init; }

    public required long LastSequence { get; init; }
}

/// <summary>
///     A page of events. <see cref="HasMore" /> rides only this feed because it is the only paged one; the client
///     follows it by re-reading from <see cref="LastSequence" />.
/// </summary>
public sealed class ListWorkSessionEventsResponse
{
    public required IReadOnlyList<WorkSessionEventResponse> Items { get; init; }

    public required long LastSequence { get; init; }

    public required bool HasMore { get; init; }
}

/// <summary>
///     Whether this node serves work sessions at all — the one response every node answers, switch on or off.
/// </summary>
/// <remarks>
///     The rest of the family is 404ed by request-path middleware when <c>WorkSessions:Enabled</c> is false, and a
///     bodyless 404 is indistinguishable from a broken route, so without this the SPA can only say "could not load".
/// </remarks>
public sealed class WorkSessionCapabilityResponse
{
    public required bool Enabled { get; init; }
}
