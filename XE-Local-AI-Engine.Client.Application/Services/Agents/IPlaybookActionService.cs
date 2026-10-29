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
    ///     Validates and applies <paramref name="input" /> to the manual action with <paramref name="id" />.
    /// </summary>
    /// <remarks>
    ///     The ownership check stops a nested-route IDOR: one agent's playbook route may not update or re-parent
    ///     another agent's action, so an action owned by another agent is reported exactly like a missing one. An
    ///     analysis-provenance action answers <see cref="PlaybookActionUpdateResult.NotManual" />: it is edited only
    ///     through the review paths.
    /// </remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of the input's agent has <paramref name="id" />.</exception>
    Task<PlaybookActionUpdateResult> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the action with <paramref name="id" /> only when it belongs to the route's
    ///     <paramref name="agentDefinitionId" />.
    /// </summary>
    /// <remarks>The same ownership guard as <see cref="UpdateAsync" />.</remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of <paramref name="agentDefinitionId" /> has <paramref name="id" />.</exception>
    Task DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

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
    ///     Human review, because staging is not active. <see cref="PlaybookPromotionResult.Promoted" /> only when the
    ///     eval passed, is complete and current and its fingerprint matches; otherwise
    ///     <see cref="PlaybookPromotionResult.Blocked" />: <c>NotPending</c>, <c>EvalRequired</c> (no eval since
    ///     authoring), <c>EvalStale</c> (older snapshot or changed context), <c>EvalIncomplete</c> (scored subset),
    ///     <c>EvalRegressed</c> (a failure) or <c>CapReached</c>.
    /// </remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of <paramref name="agentDefinitionId" /> has <paramref name="id" />.</exception>
    /// <exception cref="AgentDefinitionNotFoundException">The owning agent is gone.</exception>
    Task<PlaybookPromotionResult> PromoteSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the golden conversation eval result JSON on the pending suggestion owned by
    ///     <paramref name="agentDefinitionId" />.
    /// </summary>
    /// <remarks>
    ///     The action keeps its state and every injected field, so recording an eval never bumps <c>Version</c>: the
    ///     store excludes <c>EvalResult</c> from its config-affecting rule. Same ownership and state guard as
    ///     <see cref="PromoteSuggestedAsync" />.
    /// </remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of <paramref name="agentDefinitionId" /> has <paramref name="id" />.</exception>
    Task<PlaybookSuggestionReviewResult> RecordEvalResultAsync(Guid agentDefinitionId, Guid id, string evalResultJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Loads the pending suggestion owned by <paramref name="agentDefinitionId" /> behind the same guard the
    ///     review paths apply, or <c>null</c> when the action exists but is not a pending suggestion.
    /// </summary>
    /// <remarks>Exposed so evaluation can load the candidate snapshot without re-implementing that guard.</remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of <paramref name="agentDefinitionId" /> has <paramref name="id" />.</exception>
    Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rejects a pending suggestion owned by <paramref name="agentDefinitionId" /> by moving it to
    ///     <c>Archived</c>.
    /// </summary>
    /// <remarks>
    ///     Archived rather than hard-deleted, so provenance is preserved. Same ownership and state guard, and the
    ///     same result, as <see cref="RecordEvalResultAsync" />.
    /// </remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of <paramref name="agentDefinitionId" /> has <paramref name="id" />.</exception>
    Task<PlaybookSuggestionReviewResult> RejectSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Edits the fields of a pending suggestion before review; it keeps its state, evidence and confidence.
    /// </summary>
    /// <remarks>
    ///     Editing clears any recorded <c>EvalResult</c>, so a stale pass cannot promote an edited action. Same
    ///     ownership and state guard, and the same result, as <see cref="RecordEvalResultAsync" />.
    /// </remarks>
    /// <exception cref="PlaybookActionNotFoundException">No action of the input's agent has the input's action id.</exception>
    Task<PlaybookSuggestionReviewResult> UpdateSuggestedAsync(SuggestedActionEditInput input, CancellationToken cancellationToken = default);
}

/// <summary>Why a gated promote was blocked: the action's state, each eval-gate block, or the cap. Every value maps to 409.</summary>
public enum PlaybookPromotionStatus
{
    /// <summary>The action is not a pending suggestion: already promoted, archived, or manual.</summary>
    NotPending,
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
///     Result of <see cref="IPlaybookActionService.PromoteSuggestedAsync" />: the enabled record (200), or the reason the
///     promotion was blocked (409).
/// </summary>
public abstract record PlaybookPromotionResult
{
    public sealed record Promoted : PlaybookPromotionResult
    {
        public required PlaybookActionRecord Record { get; init; }
    }

    public sealed record Blocked : PlaybookPromotionResult
    {
        public required PlaybookPromotionStatus Status { get; init; }
    }
}

/// <summary>Result of <see cref="IPlaybookActionService.UpdateAsync" />: the updated record (200), or a wrong-state refusal (409).</summary>
public abstract record PlaybookActionUpdateResult
{
    public sealed record Updated : PlaybookActionUpdateResult
    {
        public required PlaybookActionRecord Record { get; init; }
    }

    /// <summary>The action carries analysis provenance; it is edited only through the suggestion review paths.</summary>
    public sealed record NotManual : PlaybookActionUpdateResult;
}

/// <summary>
///     Result of the suggestion review paths (record eval, edit, reject): the updated record (200), or a refusal because
///     the action is not a pending suggestion (409).
/// </summary>
public abstract record PlaybookSuggestionReviewResult
{
    public sealed record Applied : PlaybookSuggestionReviewResult
    {
        public required PlaybookActionRecord Record { get; init; }
    }

    /// <summary>The action exists but is no longer, or never was, a pending suggestion.</summary>
    public sealed record NotPending : PlaybookSuggestionReviewResult;
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

/// <summary>
///     No playbook action has the id the client supplied, or it belongs to another agent than the route's: the
///     nested-route ownership guard reports both alike.
/// </summary>
public sealed class PlaybookActionNotFoundException : ResourceNotFoundException
{
    public PlaybookActionNotFoundException(Guid id) : base($"No playbook action of this agent has id '{id}'.")
    {
    }
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
