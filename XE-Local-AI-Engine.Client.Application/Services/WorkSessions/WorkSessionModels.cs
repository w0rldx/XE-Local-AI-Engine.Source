namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>One row of the work-session list. Deliberately carries no objective — the list never renders one.</summary>
public sealed class WorkSessionSummary
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required AgentWorkSessionKind Kind { get; init; }

    public required AgentWorkSessionStatus Status { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required int StepCount { get; init; }

    public required long UpdatedUtc { get; init; }
}

/// <summary>
///     One work session in full.
/// </summary>
/// <remarks>
///     <see cref="MaxStepsPerRun" /> is the node's EFFECTIVE option value rather than a stored column, so the session
///     view renders "step N of M" without a second settings round-trip. <see cref="LastSequence" /> is the hub
///     watermark a subscriber replays from, and <see cref="Version" /> the optimistic-concurrency token a later
///     update or lifecycle call echoes back.
/// </remarks>
public sealed class WorkSessionDetail
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Objective { get; init; }

    public required AgentWorkSessionKind Kind { get; init; }

    public required AgentWorkSessionStatus Status { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid? CurrentTaskId { get; init; }

    public required int StepCount { get; init; }

    public required int MaxStepsPerRun { get; init; }

    public required Guid? LastCheckpointId { get; init; }

    public required long LastSequence { get; init; }

    public required long Version { get; init; }

    public required long CreatedUtc { get; init; }

    public required long UpdatedUtc { get; init; }
}

public sealed class WorkSessionTaskDto
{
    public required Guid Id { get; init; }

    public required Guid? ParentTaskId { get; init; }

    public required long Sequence { get; init; }

    public required string Title { get; init; }

    public required string? Detail { get; init; }

    public required AgentWorkSessionTaskStatus Status { get; init; }

    public required string? BlockedReason { get; init; }

    public required AgentWorkSessionTaskOrigin Origin { get; init; }

    public required int CreatedStep { get; init; }

    public required int UpdatedStep { get; init; }
}

public sealed class WorkSessionFindingDto
{
    public required Guid Id { get; init; }

    public required Guid? TaskId { get; init; }

    public required long Sequence { get; init; }

    public required AgentWorkSessionFindingKind Kind { get; init; }

    public required string Text { get; init; }

    public required string? SourceRef { get; init; }

    public required int CreatedStep { get; init; }

    public required bool Superseded { get; init; }
}

public sealed class WorkSessionArtifactDto
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required AgentWorkSessionArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required bool IsValid { get; init; }

    public required int CreatedStep { get; init; }
}

public sealed class WorkSessionCheckpointDto
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string? Summary { get; init; }

    public required string StateJson { get; init; }

    public required long CreatedUtc { get; init; }
}

public sealed class WorkSessionEventDto
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string EventType { get; init; }

    /// <summary>
    ///     The event's payload, opaque to this layer and shaped by whatever wrote the row — a caller parses it only
    ///     after matching on <see cref="EventType" />, and must tolerate a shape it does not know.
    /// </summary>
    /// <remarks>
    ///     Two shapes are defined today. <c>CompletionRequested</c> carries a summary and a nullable objective flag in PascalCase,
    ///     because the handler serializes it with bare defaults, and an absent or null flag means the objective WAS met, so a
    ///     completion recorded before that member existed still reads as the success it was. <c>StepEnded</c> and <c>StepFailed</c>
    ///     carry <see cref="WorkSessionStepConsumptionDetail" />, camelCase, null on a step that made no provider round at all and
    ///     without <c>toolNames</c> on a row written before that member existed.
    /// </remarks>
    public required string? DetailJson { get; init; }

    public required string? Outcome { get; init; }

    public required long OccurredUtc { get; init; }

    public required Guid? OperationId { get; init; }
}

/// <summary>What one step spent, recorded on its <c>StepEnded</c> / <c>StepFailed</c> row.</summary>
/// <remarks>
///     Counts plus a bounded set of tool NAMES — no prompts, model output, tool arguments or tool results — so the per-step
///     provider-call cap can be sized from what steps actually consume rather than from a guess. Every member is a STEP TOTAL, never
///     a turn total, and the row is per step NUMBER rather than per attempt, so aggregate these rows as a lower bound on what a
///     session cost. See <c>docs/wiki/04-agent-mode.md</c> §5.7.
/// </remarks>
/// <param name="ProviderCalls">Raw provider rounds the step admitted.</param>
/// <param name="EstimatedInputTokens">Estimated input tokens over those rounds, from the character profile rather than the provider.</param>
/// <param name="ToolCallsCompleted">Tool invocations that returned during the step, successfully or not.</param>
/// <param name="ProviderCallCap">The step's seeded cap. It bounds each invocation, not their sum — read <paramref name="AttachedBudgets" /> first.</param>
/// <param name="AttachedBudgets">How many invocations the step ran: 1 ordinarily, more when the turn spawned sub-agents.</param>
/// <param name="ToolSchemaTokens">Tool-schema tokens shipped ACROSS ROUNDS, so this grows with the round count and is not the offer's size.</param>
/// <param name="ToolNames">Distinct tool names called, ordinal-sorted, capped at sixteen. <see langword="null" /> means the row predates the member.</param>
public sealed record WorkSessionStepConsumptionDetail(
    int ProviderCalls,
    long EstimatedInputTokens,
    int ToolCallsCompleted,
    int ProviderCallCap,
    int AttachedBudgets,
    long ToolSchemaTokens = 0,
    IReadOnlyList<string>? ToolNames = null);

/// <summary>
///     An artifact's bytes as text. <see cref="IsBase64" /> is set for a media type the node cannot hand over as UTF-8,
///     so a caller never has to guess whether the payload is decodable.
/// </summary>
public sealed class WorkSessionArtifactContent
{
    public required WorkSessionArtifactDto Artifact { get; init; }

    public required string Content { get; init; }

    public required bool IsBase64 { get; init; }
}

/// <summary>
///     What ONE session runs on when its caller pins it rather than the bound agent definition: the model name and
///     the reasoning effort, either of which may be null to leave that half to the agent.
/// </summary>
/// <remarks>
///     This is a pin, not a preference: a development-workflow node authoring <c>modelProfile</c> means that node's session runs on
///     that model, so it is applied exactly the way an agent definition's own pin is, tool gate included, and a name this node cannot
///     load fails the session the same way a stale pin on the definition does. Nothing is persisted on the session row — the run's
///     graph snapshot is where a workflow node's authoring lives, and it re-supplies this on every start and resume.
/// </remarks>
public sealed class WorkSessionRuntimeOverride
{
    /// <summary>The model this session's turns run on, or null to leave that to the agent.</summary>
    public required string? ModelProfile { get; init; }

    /// <summary>The reasoning effort those turns run at, or null to leave that to the agent.</summary>
    public required string? ReasoningEffort { get; init; }

    /// <summary>
    ///     <c>GRAPH-C4-2</c>'s runtime half, carried per drive because the thing it judges is mutable. Default
    ///     <see langword="false" />, which is every caller but the workflow runtime.
    /// </summary>
    /// <remarks>
    ///     Set by a development-workflow Agent node that declares no <c>WriteExecute</c> capability and whose template waives nothing:
    ///     every turn of that session is then refused if the agent definition it re-resolves would offer a tool that writes files or
    ///     runs commands. Checked once at creation it is not checked at all, because the definition can be edited between two steps,
    ///     or deleted so the turn falls back to the default persona and its whole offer.
    /// </remarks>
    public bool RefuseUndeclaredWrites { get; init; }

    /// <summary>
    ///     Nothing pinned and nothing to enforce, which is the shape every caller but the workflow runtime has.
    /// </summary>
    /// <remarks>
    ///     The refusal flag counts: a node that pins no model and no effort still has to have its turns judged, and
    ///     the supervisor drops an override this reports empty.
    /// </remarks>
    public bool IsEmpty => string.IsNullOrWhiteSpace(ModelProfile) && string.IsNullOrWhiteSpace(ReasoningEffort) && !RefuseUndeclaredWrites;
}

/// <summary>
///     Create input. Named <c>…RequestModel</c> so it cannot collide with the store layer's
///     <c>CreateWorkSessionCommand</c>, which carries the ids and the concurrency token this one has no business
///     knowing about.
/// </summary>
public sealed class CreateWorkSessionRequestModel
{
    public required string Title { get; init; }

    public required string Objective { get; init; }

    public required AgentWorkSessionKind Kind { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public WorkSessionRuntimeOverride? Runtime { get; init; }
}

/// <summary>Update input. A null member leaves the stored value alone.</summary>
public sealed class UpdateWorkSessionRequestModel
{
    public required string? Title { get; init; }

    public required string? Objective { get; init; }

    public required Guid? AgentDefinitionId { get; init; }
}

/// <summary>
///     The completion request the supervisor reads back at step end, as it is written to the event log.
/// </summary>
/// <remarks>
///     <see cref="ObjectiveMet" /> is nullable rather than defaulted so an event recorded before the argument existed
///     reads as <see langword="null" /> — absent, and therefore met — instead of as an unmet objective the model
///     never declared. Only an explicit <see langword="false" /> stands a workflow-owned node run down.
/// </remarks>
internal sealed record WorkSessionCompletionDetail(string Summary, bool? ObjectiveMet = null);
