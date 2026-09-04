namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Everything a trigger create carries. <see cref="Name" /> is the external contract a caller addresses, so it is
///     present here and absent from <see cref="IntegrationTriggerUpdateInput" />: renaming a live trigger is a
///     delete-and-create decision, not an edit.
/// </summary>
public sealed record IntegrationTriggerCreateInput(
    string Name,
    string DisplayName,
    string? Description,
    bool Enabled,
    IntegrationTargetKind TargetKind,
    Guid TargetAgentDefinitionId,
    IntegrationSessionPolicy SessionPolicy,
    IntegrationInputKinds AcceptedInputKinds);

/// <summary>An optimistic update. <see cref="ExpectedVersion" /> is the caller's copy of the row's concurrency token.</summary>
public sealed record IntegrationTriggerUpdateInput(
    long ExpectedVersion,
    string DisplayName,
    string? Description,
    bool Enabled,
    Guid TargetAgentDefinitionId,
    IntegrationSessionPolicy SessionPolicy,
    IntegrationInputKinds AcceptedInputKinds);

/// <summary>
///     What a trigger write decided. Every non-<see cref="Saved" /> value maps to exactly one HTTP status at the
///     endpoint, which is why the service returns an outcome rather than throwing: none of these is exceptional.
/// </summary>
public enum IntegrationTriggerOutcome
{
    /// <summary>The row was written; the result carries it.</summary>
    Saved,

    /// <summary>Another trigger already owns the normalised name. 409.</summary>
    NameConflict,

    /// <summary>The target agent definition does not exist. 400.</summary>
    AgentMissing,

    /// <summary>The target agent's tool offer is not read-only, so it cannot host caller-managed sessions. 400.</summary>
    SessionPolicyRejected,

    /// <summary>The target agent is an orchestrator, and ruling D2 scopes V1 to a saved single agent. 400.</summary>
    TargetKindRejected,

    /// <summary>No row with that id. 404.</summary>
    NotFound,

    /// <summary>The row moved on since the caller read it. 409.</summary>
    VersionConflict
}

/// <summary><see cref="Trigger" /> is non-null exactly when <see cref="Outcome" /> is <see cref="IntegrationTriggerOutcome.Saved" />.</summary>
public sealed record IntegrationTriggerResult(IntegrationTriggerOutcome Outcome, IntegrationTriggerSnapshot? Trigger, string? Message);

/// <summary>
///     Trigger CRUD with the two checks a validator cannot make because they need the store and the agent resolver:
///     the target agent must exist, and a <c>CallerManaged</c> trigger's agent must offer read-only tools only.
///     <para>
///         The caller-managed check here is a PREFLIGHT. An agent's tool offer can change after the trigger is saved,
///         so the accept path repeats it and is the authority (ruling R4-9(a)).
///     </para>
/// </summary>
public interface IIntegrationTriggerService
{
    Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<IntegrationTriggerSnapshot?> GetAsync(Guid triggerId, CancellationToken cancellationToken = default);

    Task<IntegrationTriggerResult> CreateAsync(IntegrationTriggerCreateInput input, CancellationToken cancellationToken = default);

    Task<IntegrationTriggerResult> UpdateAsync(Guid triggerId, IntegrationTriggerUpdateInput input, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ruling R4-9(a)'s predicate, run against the agent's offer AS IT IS NOW: a caller-managed session may only
    ///     target an agent whose every resolved tool is <c>ToolCategory.ReadLocal</c>. Used at trigger save AND on the
    ///     accept path, because an agent definition's tools can change between the two and only the accept path sees
    ///     the definition as it is at invocation time.
    ///     <para>
    ///         The offer is resolved against the SAME effective model the coordinator picks (the agent's pin, else the
    ///         node's local default), because the offer is model-gated: resolving with no model would yield a NARROWER
    ///         set than the run and pass a trigger the accept path then refuses.
    ///     </para>
    ///     <para>
    ///         Fails CLOSED — a definition that cannot be resolved is not allowed. It also needs no special case for
    ///         the seeded Default Assistant: that definition receives the WHOLE capability-gated offer regardless of
    ///         its (empty) <c>AllowedToolNames</c>, so the resolved offer this reads already carries the
    ///         orchestration and write tools that refuse it.
    ///     </para>
    /// </summary>
    Task<bool> AllowsCallerManagedAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The bare half of <see cref="AllowsCallerManagedAsync" />, for a caller that has ALREADY resolved the offer —
    ///     the execution coordinator, which must judge the very offer its package will carry rather than a second
    ///     resolve that could disagree. <c>ToolCategory.Unknown</c> is the fail-closed default for an uncategorised
    ///     tool, so the predicate is "not ReadLocal" rather than "is WriteExecute". Approval-gated tools do NOT trip it:
    ///     they stay in the offer and fail loudly at the call (ruling R4-5).
    /// </summary>
    static bool AllowsCallerManaged(IReadOnlyList<AllowedToolDto>? resolvedTools) =>
        resolvedTools is not null && !resolvedTools.Any(static tool => tool.Category != ToolCategory.ReadLocal);

    /// <summary>
    ///     Normalises an external trigger name the one way the whole feature agrees on: trimmed and lowercased. Both
    ///     the admin writes and the invoke lookup call it, so a name saved from the UI and a name typed into a curl
    ///     command resolve to the same row.
    /// </summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "The trigger name is lowercase BY CONTRACT (^[a-z0-9][a-z0-9-]{1,63}$) and is the external route segment a caller types, not a security identifier that must round-trip.")]
    static string NormalizeName(string? name) =>
        name?.Trim().ToLowerInvariant() ?? string.Empty;
}
