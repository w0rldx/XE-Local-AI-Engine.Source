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
///     <para>
///         <see cref="MaxStepsPerRun" /> is the node's EFFECTIVE option value rather than a stored column, so the
///         session view can render "step N of M" without a second settings round-trip. <see cref="LastSequence" /> is
///         the hub watermark a subscriber replays from; <see cref="Version" /> is the optimistic-concurrency token a
///         later update or lifecycle call echoes back.
///     </para>
/// </summary>
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
    ///     The event's payload, opaque to this layer and shaped by whatever wrote the row — a caller parses it only after
    ///     matching on <see cref="EventType" />, and must tolerate a shape it does not know.
    ///     <para>
    ///         Two shapes are defined today. <c>CompletionRequested</c> carries
    ///         <c>{ "Summary": string, "ObjectiveMet": bool? }</c> — PascalCase, because the handler serializes it with
    ///         bare defaults — where an absent or null <c>ObjectiveMet</c> means the objective WAS met, so a completion
    ///         recorded before that member existed still reads as the success it was.
    ///         <c>StepEnded</c> and <c>StepFailed</c> carry the step's content-free consumption record —
    ///         <see cref="WorkSessionStepConsumptionDetail" />, i.e.
    ///         <c>{ "providerCalls": int, "estimatedInputTokens": long, "toolCallsCompleted": int, "providerCallCap": int,
    ///         "attachedBudgets": int, "toolSchemaTokens": long, "toolNames": string[] }</c>. It is null on a step that
    ///         made no provider round at all, and <c>toolNames</c> is absent on a row written before that member existed.
    ///         Counts plus tool NAMES: no prompt, no model output, no tool argument and no tool result.
    ///     </para>
    /// </summary>
    public required string? DetailJson { get; init; }

    public required string? Outcome { get; init; }

    public required long OccurredUtc { get; init; }

    public required Guid? OperationId { get; init; }
}

/// <summary>
///     What one step spent, recorded on its <c>StepEnded</c> / <c>StepFailed</c> row so the per-step provider-call cap
///     can be sized from what steps actually consume rather than from a guess. Counts plus a bounded set of tool NAMES
///     — no prompts, no model output, no tool arguments and no tool results — so it is safe to persist and to show.
///     <para>
///         The names are here because this row is the only DURABLE carrier they have. The scope they are collected in
///         is disposed at the end of the step that seeded it, and anything asking later — a Dev Workflow node run
///         settling on a later dispatcher tick, in another scope and possibly another process — can read only what was
///         persisted. A name is an identity, not content: a fixed id for a built-in tool and an operator-authored
///         identifier for an MCP or custom one.
///     </para>
///     <para>
///         Every member is a STEP TOTAL read off the step's own cap scope, which is why the provider's own reported
///         token usage is not among them: it is a TURN number — the last round's counts on the message, the rounds'
///         sum on the run envelope — and a step is not the same denominator as a turn.
///         Estimate-versus-truth is measured per round instead, where both halves describe the same request, by
///         <c>ProviderCallBudgetChatClient</c>'s observed-usage write-back into the calibration store.
///     </para>
///     <para>
///         <b><see cref="ProviderCalls" /> is a ratio against <see cref="ProviderCallCap" /> only while
///         <see cref="AttachedBudgets" /> is 1.</b> The cap bounds each invocation separately, and a step that spawned
///         sub-agents ran more than one — so eighteen calls across two budgets is two runs that each stayed under ten,
///         not one run that breached it. Read the two together or the record argues for raising a cap nothing hit.
///     </para>
///     <para>
///         The row is per STEP NUMBER, not per attempt. The supervisor derives its event operation id from the session
///         and the step, so a step replayed after a crash finds the first attempt's row already recorded and the
///         store's idempotency returns it unchanged — the numbers on the row are the ones the FIRST attempt spent,
///         and the replay's own spend is not added to them and not recorded anywhere else. Aggregate these rows as a
///         lower bound on what a session cost, not as an exact total.
///     </para>
/// </summary>
/// <param name="ProviderCalls">Raw provider rounds the step admitted. Against <paramref name="ProviderCallCap" /> this is the number that matters.</param>
/// <param name="EstimatedInputTokens">Estimated input tokens summed over those rounds, from the character profile rather than the provider.</param>
/// <param name="ToolCallsCompleted">Tool invocations that returned during the step, successfully or not.</param>
/// <param name="ProviderCallCap">
///     The cap the step was seeded with (<c>WorkSessions:MaxProviderCallsPerStep</c>). It bounds each invocation, not
///     their sum — see <paramref name="AttachedBudgets" /> before treating it as a denominator.
/// </param>
/// <param name="AttachedBudgets">
///     How many invocations the step ran: 1 ordinarily, more when the turn spawned sub-agents, each with its own cap.
/// </param>
/// <param name="ToolSchemaTokens">
///     Tool-schema tokens SHIPPED ACROSS ROUNDS — every round re-sends the whole offer, so this grows with the round
///     count and is not the size of the offer.
/// </param>
/// <param name="ToolNames">
///     The distinct tool names the step called, ordinal-sorted and capped at sixteen. Trailing and optional: a row
///     written before this member existed reads back as <see langword="null" />, which means "this row predates the
///     field", never "this step called no tools" — <paramref name="ToolCallsCompleted" /> answers that.
/// </param>
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
///     What ONE session runs on when its caller pins it rather than the bound agent definition: the model name and the
///     reasoning effort, either of which may be null to leave that half to the agent.
///     <para>
///         This is a pin, not a preference. A development-workflow node authoring <c>modelProfile</c> means that node's
///         session runs on that model — so it is applied exactly the way an agent definition's own pin is, tool gate
///         included, and a name this node cannot load fails the session the same way a stale pin on the definition
///         does. Nothing is persisted on the session row: the run's graph snapshot is where a workflow node's authoring
///         lives, and it re-supplies this on every start and resume.
///     </para>
/// </summary>
public sealed class WorkSessionRuntimeOverride
{
    /// <summary>The model this session's turns run on, or null to leave that to the agent.</summary>
    public required string? ModelProfile { get; init; }

    /// <summary>The reasoning effort those turns run at, or null to leave that to the agent.</summary>
    public required string? ReasoningEffort { get; init; }

    /// <summary>
    ///     <c>GRAPH-C4-2</c>'s runtime half, carried per drive because the thing it judges is mutable. Set by a
    ///     development-workflow Agent node that declares no <c>WriteExecute</c> capability and whose template waives
    ///     nothing: every turn of that session must then be refused if the agent definition it re-resolves would offer a
    ///     tool that writes files or runs commands. Checked once at creation it is not checked at all — the definition can
    ///     be edited between two steps, or deleted so the turn falls back to the default persona and its whole offer.
    ///     <para>
    ///         Default <see langword="false" />, which is every other caller and today's behaviour exactly.
    ///     </para>
    /// </summary>
    public bool RefuseUndeclaredWrites { get; init; }

    /// <summary>
    ///     Nothing pinned and nothing to enforce, which is the shape every caller but the workflow runtime has. The
    ///     refusal flag counts: a node that pins no model and no effort still has to have its turns judged, and the
    ///     supervisor drops an override this reports empty.
    /// </summary>
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
///     <para>
///         <see cref="ObjectiveMet" /> is nullable rather than defaulted so that an event recorded before the argument
///         existed reads as <see langword="null" /> — absent, and therefore met — instead of as an unmet objective the
///         model never declared. Only an explicit <see langword="false" /> stands a workflow-owned node run down.
///     </para>
/// </summary>
internal sealed record WorkSessionCompletionDetail(string Summary, bool? ObjectiveMet = null);
