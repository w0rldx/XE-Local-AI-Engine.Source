namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Application-layer orchestration over <see cref="IPlaybookActionStore" />: validates the supplied fields and
///     delegates persistence. The store owns id/version/timestamp stamping and the config-affecting version-bump rule;
///     this service never re-implements versioning. Validation rejects a blank Behavior, an unknown owning agent, and
///     the lifecycle/provenance states reserved for later phases (P1 accepts only <c>Enabled</c>/<c>Disabled</c> and
///     forces <c>Source = Manual</c>).
/// </summary>
public interface IPlaybookActionService
{
    /// <summary>Validates and persists a new playbook action, returning the stored record.</summary>
    Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="input" /> to the action with <paramref name="id" />. Returns the
    ///     updated record, or <c>null</c> when no action has that id <b>or</b> when the action belongs to a different
    ///     agent than the one named on <paramref name="input" /> (<c>AgentDefinitionId</c>). The ownership check stops a
    ///     nested-route IDOR — one agent's playbook route may not update or re-parent another agent's action.
    /// </summary>
    Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the action with <paramref name="id" /> only when it belongs to <paramref name="agentDefinitionId" />
    ///     (the agent named on the route). Returns <c>true</c> when a row was deleted, <c>false</c> when no action has
    ///     that id or it belongs to a different agent — the same ownership guard as <see cref="UpdateAsync" />.
    /// </summary>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no action has that id.</summary>
    Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every action for <paramref name="agentDefinitionId" />, ordered by Priority then CreatedAtUtc.</summary>
    Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Playbook P3 analysis write path (separate from the manual <see cref="CreateAsync" /> so the manual route stays
    ///     pinned to <c>Manual</c>/<c>Enabled</c>/<c>Disabled</c>). Persists a new action in state <c>Suggested</c> /
    ///     source <c>Analysis</c> with its evidence (<c>SourceFeedbackIds</c>) and <c>Confidence</c>. Validates a
    ///     non-blank Behavior, an existing owning agent, non-empty evidence, and a confidence in [0,1]. A
    ///     <c>Suggested</c> action is inert by construction — the resolver injects only <c>Enabled</c> actions.
    /// </summary>
    Task<PlaybookActionRecord> CreateAnalysisSuggestionAsync(PlaybookAnalysisSuggestionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Promotes a <c>Suggested</c>/<c>Analysis</c> action owned by <paramref name="agentDefinitionId" /> to
    ///     <c>Enabled</c> (human review — staging ≠ active), gated by the Playbook P4 eval result. Returns a
    ///     <see cref="PlaybookPromotionResult" /> whose <see cref="PlaybookPromotionResult.Status" /> is
    ///     <c>NotFound</c> when the action is missing/cross-agent/not a pending suggestion, <c>EvalRequired</c> when no
    ///     eval has run since authoring/edit, <c>EvalStale</c> when the recorded eval is for an older content snapshot,
    ///     <c>EvalRegressed</c> when the latest eval failed, and <c>Promoted</c> (with the updated record) only when the
    ///     latest eval passed and is current.
    /// </summary>
    Task<PlaybookPromotionResult> PromoteSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the Playbook P4 eval result JSON on the pending <c>Suggested</c>/<c>Analysis</c> action owned by
    ///     <paramref name="agentDefinitionId" />. The action stays <c>Suggested</c>/<c>Analysis</c> with all injected
    ///     fields (Behavior/Priority/State) unchanged, so recording an eval never bumps <c>Version</c> (the store
    ///     excludes <c>EvalResult</c> from its config-affecting rule). Same ownership/state guard and <c>null</c>
    ///     contract as <see cref="PromoteSuggestedAsync" />.
    /// </summary>
    Task<PlaybookActionRecord?> RecordEvalResultAsync(Guid agentDefinitionId, Guid id, string evalResultJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Loads the pending suggestion owned by <paramref name="agentDefinitionId" /> after the same ownership +
    ///     <c>Suggested</c> + <c>Analysis</c> guard the review paths apply, or <c>null</c> when no such pending
    ///     suggestion exists. Exposed so the eval gate can load the candidate snapshot without re-implementing the guard.
    /// </summary>
    Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rejects a <c>Suggested</c>/<c>Analysis</c> action owned by <paramref name="agentDefinitionId" /> by moving it
    ///     to <c>Archived</c> (provenance is preserved rather than hard-deleted). Same ownership/state guard and
    ///     <c>null</c> contract as <see cref="RecordEvalResultAsync" />.
    /// </summary>
    Task<PlaybookActionRecord?> RejectSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Edits the fields of a pending <c>Suggested</c>/<c>Analysis</c> action before review (it stays
    ///     <c>Suggested</c>/<c>Analysis</c> and keeps its evidence/confidence). Editing clears any recorded
    ///     <c>EvalResult</c> so a stale pass cannot promote an edited action. Same ownership/state guard and <c>null</c>
    ///     contract as <see cref="RecordEvalResultAsync" />.
    /// </summary>
    Task<PlaybookActionRecord?> UpdateSuggestedAsync(SuggestedActionEditInput input, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a gated promote: distinguishes a 404 (NotFound) from each eval-gate block, and a success.</summary>
public enum PlaybookPromotionStatus
{
    Promoted,
    NotFound,
    EvalRequired,
    EvalRegressed,
    EvalStale
}

/// <summary>
///     Result of <see cref="IPlaybookActionService.PromoteSuggestedAsync" />: <see cref="Status" /> tells the endpoint
///     whether to return 200 (<c>Promoted</c>), 404 (<c>NotFound</c>) or 409 (any <c>Eval*</c> block); <see cref="Record" />
///     carries the enabled record only when <see cref="Status" /> is <c>Promoted</c>.
/// </summary>
public sealed record PlaybookPromotionResult(PlaybookPromotionStatus Status, PlaybookActionRecord? Record);

/// <summary>Input for the P3 analysis write path — provenance + confidence are required; state/source are pinned by the service.</summary>
public sealed record PlaybookAnalysisSuggestionInput(
    Guid AgentDefinitionId,
    string Behavior,
    string? TriggerCondition,
    string? Scope,
    int Priority,
    IReadOnlyList<Guid> SourceFeedbackIds,
    double Confidence);

/// <summary>Operator edits applied to a pending Suggested action; the action stays Suggested/Analysis and keeps its evidence.</summary>
public sealed record SuggestedActionEditInput(
    Guid AgentDefinitionId,
    Guid ActionId,
    string Behavior,
    string? TriggerCondition,
    string? Scope,
    int Priority);

/// <summary>Thrown when a playbook-action create/update fails validation. The message is safe to surface to callers.</summary>
public sealed class PlaybookActionValidationException : Exception
{
    public PlaybookActionValidationException(string message) : base(message)
    {
    }

    public PlaybookActionValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
