namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>One row of the work-session list. Deliberately carries no objective — the list never renders one.</summary>
public sealed record WorkSessionSummary(
    Guid Id,
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
public sealed record WorkSessionDetail(
    Guid Id,
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

public sealed record WorkSessionTaskDto(
    Guid Id,
    Guid? ParentTaskId,
    long Sequence,
    string Title,
    string? Detail,
    AgentWorkSessionTaskStatus Status,
    string? BlockedReason,
    AgentWorkSessionTaskOrigin Origin,
    int CreatedStep,
    int UpdatedStep);

public sealed record WorkSessionFindingDto(
    Guid Id,
    Guid? TaskId,
    long Sequence,
    AgentWorkSessionFindingKind Kind,
    string Text,
    string? SourceRef,
    int CreatedStep,
    bool Superseded);

public sealed record WorkSessionArtifactDto(
    Guid Id,
    long Sequence,
    AgentWorkSessionArtifactKind Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    bool IsValid,
    int CreatedStep);

public sealed record WorkSessionCheckpointDto(
    Guid Id,
    long Sequence,
    int Step,
    string? Summary,
    string StateJson,
    long CreatedUtc);

/// <param name="DetailJson">
///     The event's payload, opaque to this layer and shaped by whatever wrote the row — a caller parses it only after
///     matching on <paramref name="EventType" />, and must tolerate a shape it does not know.
///     <para>
///         Two shapes are defined today. <c>CompletionRequested</c> carries <c>{ "summary": string }</c>.
///         <c>StepEnded</c> and <c>StepFailed</c> carry the step's consumption record —
///         <see cref="WorkSessionStepConsumptionDetail" />, i.e.
///         <c>{ "providerCalls": int, "estimatedInputTokens": long, "toolCallsCompleted": int, "providerCallCap": int,
///         "attachedBudgets": int, "toolSchemaTokens": long, "toolNames": string[] }</c>. It is null on a step that
///         made no provider round at all, and <c>toolNames</c> is absent on a row written before that member existed.
///         Counts plus tool NAMES: no prompt, no model output, no tool argument and no tool result.
///     </para>
/// </param>
public sealed record WorkSessionEventDto(
    Guid Id,
    long Sequence,
    int Step,
    string EventType,
    string? DetailJson,
    string? Outcome,
    long OccurredUtc,
    Guid? OperationId);

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
///         Every member is a STEP TOTAL, which is why the provider's own reported token usage is not among them: the
///         invocation runner assigns its <c>UsageSnapshot</c> per provider round rather than accumulating, so on a
///         multi-round step it holds the LAST round's numbers and would silently contradict the totals beside it.
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
public sealed record WorkSessionArtifactContent(WorkSessionArtifactDto Artifact, string Content, bool IsBase64);

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
public sealed record WorkSessionRuntimeOverride(string? ModelProfile, string? ReasoningEffort)
{
    /// <summary>Nothing pinned, which is the shape every caller but the workflow runtime has.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(ModelProfile) && string.IsNullOrWhiteSpace(ReasoningEffort);
}

/// <summary>
///     Create input. Named <c>…RequestModel</c> so it cannot collide with the store layer's
///     <c>CreateWorkSessionCommand</c>, which carries the ids and the concurrency token this one has no business
///     knowing about.
/// </summary>
public sealed record CreateWorkSessionRequestModel(string Title,
    string Objective,
    AgentWorkSessionKind Kind,
    Guid AgentDefinitionId,
    WorkSessionRuntimeOverride? Runtime = null);

/// <summary>Update input. A null member leaves the stored value alone.</summary>
public sealed record UpdateWorkSessionRequestModel(string? Title, string? Objective, Guid? AgentDefinitionId);
