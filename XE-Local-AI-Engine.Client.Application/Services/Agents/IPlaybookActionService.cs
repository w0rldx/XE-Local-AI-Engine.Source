namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over <see cref="IPlaybookActionStore" />: it validates the supplied fields and
///     delegates persistence.
/// </summary>
/// <remarks>
///     The store owns id, version and timestamp stamping and the config-affecting version-bump rule; this service
///     never re-implements versioning. Validation rejects a blank Behavior, an unknown owning agent and the
///     lifecycle and provenance states reserved for analysis review: manual authoring accepts only <c>Enabled</c> or
///     <c>Disabled</c> and forces a <c>Manual</c> source.
/// </remarks>
public interface IPlaybookActionService
{
    /// <summary>Validates and persists a new playbook action, returning the stored record.</summary>
    Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="input" /> to the action with <paramref name="id" />, or answers
    ///     <c>null</c> when no action has that id or it belongs to another agent.
    /// </summary>
    /// <remarks>
    ///     The ownership check stops a nested-route IDOR: one agent's playbook route may not update or re-parent
    ///     another agent's action.
    /// </remarks>
    Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the action with <paramref name="id" /> only when it belongs to the route's
    ///     <paramref name="agentDefinitionId" />.
    /// </summary>
    /// <remarks>
    ///     <c>true</c> when a row was deleted, <c>false</c> when no action has that id or it belongs to a different
    ///     agent — the same ownership guard as <see cref="UpdateAsync" />.
    /// </remarks>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no action has that id.</summary>
    Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every action for <paramref name="agentDefinitionId" />, ordered by Priority then CreatedAtUtc.</summary>
    Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Analysis-staging write path: persists a new action as <c>Suggested</c>/<c>Analysis</c> with its evidence
    ///     and confidence.
    /// </summary>
    /// <remarks>
    ///     Separate from the manual <see cref="CreateAsync" /> route, which stays pinned to
    ///     <c>Manual</c>/<c>Enabled</c>/<c>Disabled</c>. It validates a non-blank Behavior, an existing owning agent,
    ///     non-empty evidence and a confidence in [0,1]. A <c>Suggested</c> action is inert by construction: the
    ///     resolver injects only <c>Enabled</c> ones.
    /// </remarks>
    Task<PlaybookActionRecord> CreateAnalysisSuggestionAsync(PlaybookAnalysisSuggestionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Promotes a pending suggestion owned by <paramref name="agentDefinitionId" /> to <c>Enabled</c>, gated by
    ///     the golden conversation eval result.
    /// </summary>
    /// <remarks>
    ///     Human review, because staging is not active. <see cref="PlaybookPromotionResult.Status" /> is
    ///     <c>NotFound</c> for a missing, cross-agent or non-pending action, <c>EvalRequired</c> with no eval since
    ///     authoring, <c>EvalStale</c> for an older snapshot or changed behaviour-affecting context,
    ///     <c>EvalIncomplete</c> for a scored subset, <c>EvalRegressed</c> for a failure, <c>CapReached</c> at the
    ///     cap, and <c>Promoted</c> only when the eval passed, is complete and current and its fingerprint matches.
    /// </remarks>
    Task<PlaybookPromotionResult> PromoteSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the golden conversation eval result JSON on the pending suggestion owned by
    ///     <paramref name="agentDefinitionId" />.
    /// </summary>
    /// <remarks>
    ///     The action keeps its state and every injected field, so recording an eval never bumps <c>Version</c>: the
    ///     store excludes <c>EvalResult</c> from its config-affecting rule. Same ownership and state guard, and the
    ///     same <c>null</c> contract, as <see cref="PromoteSuggestedAsync" />.
    /// </remarks>
    Task<PlaybookActionRecord?> RecordEvalResultAsync(Guid agentDefinitionId, Guid id, string evalResultJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Loads the pending suggestion owned by <paramref name="agentDefinitionId" /> behind the same guard the
    ///     review paths apply, or <c>null</c> when none exists.
    /// </summary>
    /// <remarks>Exposed so evaluation can load the candidate snapshot without re-implementing that guard.</remarks>
    Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rejects a pending suggestion owned by <paramref name="agentDefinitionId" /> by moving it to
    ///     <c>Archived</c>.
    /// </summary>
    /// <remarks>
    ///     Archived rather than hard-deleted, so provenance is preserved. Same ownership and state guard, and the
    ///     same <c>null</c> contract, as <see cref="RecordEvalResultAsync" />.
    /// </remarks>
    Task<PlaybookActionRecord?> RejectSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Edits the fields of a pending suggestion before review; it keeps its state, evidence and confidence.
    /// </summary>
    /// <remarks>
    ///     Editing clears any recorded <c>EvalResult</c>, so a stale pass cannot promote an edited action. Same
    ///     ownership and state guard, and the same <c>null</c> contract, as <see cref="RecordEvalResultAsync" />.
    /// </remarks>
    Task<PlaybookActionRecord?> UpdateSuggestedAsync(SuggestedActionEditInput input, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a gated promote: distinguishes a 404 (NotFound) from each eval-gate block, the cap block, and a success.</summary>
public enum PlaybookPromotionStatus
{
    Promoted,
    NotFound,
    EvalRequired,
    EvalRegressed,
    EvalStale,

    /// <summary>
    ///     The recorded eval scored only a SUBSET of the enabled golden cases, the per-run <c>MaxGoldenCases</c> cap
    ///     having truncated the set. Maps to 409.
    /// </summary>
    /// <remarks>
    ///     A subset pass cannot prove no-regression across the whole suite, so the operator must raise the cap and
    ///     re-run a complete eval before promoting.
    /// </remarks>
    EvalIncomplete,

    /// <summary>
    ///     The agent is already at <c>MaxEnabledActions</c>. Maps to 409.
    /// </summary>
    /// <remarks>
    ///     The promote is blocked with no store write; the operator must archive or disable an Enabled action before
    ///     promoting another.
    /// </remarks>
    CapReached
}

/// <summary>
///     Result of <see cref="IPlaybookActionService.PromoteSuggestedAsync" />: <see cref="Status" /> tells the endpoint
///     whether to return 200 (<c>Promoted</c>), 404 (<c>NotFound</c>) or 409 (any <c>Eval*</c> block, or <c>CapReached</c>);
///     <see cref="Record" /> carries the enabled record only when <see cref="Status" /> is <c>Promoted</c>.
/// </summary>
public sealed class PlaybookPromotionResult
{
    public required PlaybookPromotionStatus Status { get; init; }

    public required PlaybookActionRecord? Record { get; init; }
}

/// <summary>Input for the analysis-staging write path — provenance + confidence are required; state/source are pinned by the service.</summary>
public sealed record PlaybookAnalysisSuggestionInput
{
    public required Guid AgentDefinitionId { get; init; }

    public required string Behavior { get; init; }

    public required string? TriggerCondition { get; init; }

    public required string? Scope { get; init; }

    public required int Priority { get; init; }

    public required IReadOnlyList<Guid> SourceFeedbackIds { get; init; }

    public required double Confidence { get; init; }
}

/// <summary>Operator edits applied to a pending Suggested action; the action stays Suggested/Analysis and keeps its evidence.</summary>
public sealed class SuggestedActionEditInput
{
    public required Guid AgentDefinitionId { get; init; }

    public required Guid ActionId { get; init; }

    public required string Behavior { get; init; }

    public required string? TriggerCondition { get; init; }

    public required string? Scope { get; init; }

    public required int Priority { get; init; }
}

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
