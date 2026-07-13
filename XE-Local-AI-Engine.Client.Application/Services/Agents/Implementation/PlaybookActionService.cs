namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;

internal sealed class PlaybookActionService(
    IPlaybookActionStore store,
    IAgentDefinitionStore agentDefinitionStore,
    IGoldenConversationStore goldenConversationStore,
    IOptions<PlaybookActionOptions> actionOptions,
    IOptions<PlaybookEvalOptions> evalOptions) : IPlaybookActionService
{
    private readonly PlaybookActionOptions _actionOptions = (actionOptions ?? throw new ArgumentNullException(nameof(actionOptions))).Value;
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly PlaybookEvalOptions _evalOptions = (evalOptions ?? throw new ArgumentNullException(nameof(evalOptions))).Value;
    private readonly IGoldenConversationStore _goldenConversationStore = goldenConversationStore ?? throw new ArgumentNullException(nameof(goldenConversationStore));
    private readonly IPlaybookActionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);

        // Manual create-as-Enabled is the second path into Enabled (alongside promote); enforce the same hard cap so it
        // cannot be bypassed via direct CRUD. A create-as-Disabled never touches the cap.
        if (input.State == PlaybookActionState.Enabled)
        {
            await EnsureBelowEnabledCapAsync(input.AgentDefinitionId, excludedActionId: null, cancellationToken).ConfigureAwait(false);
        }

        return await _store.AddAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);

        // Ownership guard: the action must already belong to the agent named on the route (input.AgentDefinitionId).
        // A mismatch (or a missing action) returns null, which the endpoint maps to 404 — this blocks updating or
        // re-parenting another agent's action through this agent's nested playbook route (IDOR).
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != input.AgentDefinitionId)
        {
            return null;
        }

        // The manual route never touches an analysis-provenance action: the mapper pins Source = Manual on the input,
        // so letting it update an Analysis action would silently rewrite its provenance to Manual and drop its
        // evidence. Analysis (Suggested/Archived) actions are edited only via the dedicated analysis-review paths.
        if (existing.Source != PlaybookActionSource.Manual)
        {
            return null;
        }

        // Manual Disabled->Enabled is a transition INTO Enabled; enforce the hard cap (excluding this action from the
        // count). Editing an action that is already Enabled (stays Enabled) is not a transition and is never blocked.
        if (existing.State != PlaybookActionState.Enabled && input.State == PlaybookActionState.Enabled)
        {
            await EnsureBelowEnabledCapAsync(input.AgentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        }

        return await _store.UpdateAsync(id, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Same ownership guard as UpdateAsync: only delete the action when it belongs to the route agent, so one
        // agent's playbook route cannot delete another agent's action.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != agentDefinitionId)
        {
            return false;
        }

        return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        return _store.ListByAgentAsync(agentDefinitionId, cancellationToken);
    }

    public async Task<PlaybookActionRecord> CreateAnalysisSuggestionAsync(PlaybookAnalysisSuggestionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Behavior))
        {
            throw new PlaybookActionValidationException("Behavior is required.");
        }

        // An analysis proposal with no cited evidence is rejected, never stored.
        if (input.SourceFeedbackIds is null || input.SourceFeedbackIds.Count == 0)
        {
            throw new PlaybookActionValidationException("An analysis suggestion must cite at least one source feedback id.");
        }

        if (double.IsNaN(input.Confidence) || input.Confidence is < 0d or > 1d)
        {
            throw new PlaybookActionValidationException("Confidence must be between 0 and 1.");
        }

        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }

        // State/Source are pinned here (Suggested/Analysis) — never client-supplied — so the manual CRUD route stays
        // the only path that authors Manual actions, and a suggestion stays inert until a human promotes it.
        var storeInput = new PlaybookActionInput(input.AgentDefinitionId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            input.TriggerCondition,
            input.Behavior,
            input.Scope,
            input.Priority,
            input.SourceFeedbackIds,
            input.Confidence);

        return await _store.AddAsync(storeInput, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybookPromotionResult> PromoteSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Human-approved staging → active, gated by the golden-conversation evaluation result. The ownership/state
        // guard runs first (NotFound → 404), then evaluation status (Eval* → 409), and only a passed, current eval flips Enabled.
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.NotFound, Record: null);
        }

        // No eval since authoring/edit → promotion is not yet provable. (UpdateSuggestedAsync clears EvalResult on edit.)
        if (string.IsNullOrWhiteSpace(pending.EvalResult))
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, Record: null);
        }

        PlaybookEvalResult? evalResult;
        try
        {
            evalResult = JsonSerializer.Deserialize<PlaybookEvalResult>(pending.EvalResult, PlaybookEvalResult.SerializerOptions);
        }
        catch (JsonException)
        {
            // A result we cannot read cannot prove no-regression; require a fresh eval rather than promote blindly.
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, Record: null);
        }

        if (evalResult is null)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, Record: null);
        }

        // Staleness backstop behind clear-on-edit: the recorded pass must be for the action's current content snapshot.
        if (evalResult.ActionVersionAtEval != pending.Version)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalStale, Record: null);
        }

        // Completeness: a run that evaluated only a subset of the enabled golden cases (the per-run cap truncated the
        // set) cannot prove no-regression across the whole suite, so a subset pass never authorizes promotion.
        if (evalResult.GoldenCaseCount < evalResult.GoldenCaseTotal)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalIncomplete, Record: null);
        }

        // Fingerprint: the recorded eval must reflect the CURRENT behaviour-affecting context. Recompute the fingerprint
        // over the agent's base instructions, sibling enabled actions, the enabled golden set, and the eval model, and
        // require it to match — so a base-instruction / golden-set / sibling-action / model change after the eval ran
        // (which does not bump the action's own version) blocks the promote. A legacy result with no recorded
        // fingerprint fails this match and is treated as stale (re-run), which is the safe direction.
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.NotFound, Record: null);
        }

        var enabledActions = await _store.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        var enabledGoldenCases = await _goldenConversationStore.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        var currentFingerprint = PlaybookEvalFingerprint.Compute(pending.Id,
            pending.Version,
            owningAgent.Instructions,
            enabledActions,
            enabledGoldenCases,
            _evalOptions.ModelName);
        if (!string.Equals(currentFingerprint, evalResult.EvaluationFingerprint, StringComparison.Ordinal))
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalStale, Record: null);
        }

        if (!evalResult.Passed)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRegressed, Record: null);
        }

        // Hard cap on enabled actions: the eval may pass, but if the agent is already at MaxEnabledActions the
        // promote is blocked with no store write — the operator archives/disables an Enabled action first. The pending
        // suggestion is not yet Enabled, so the count needs no exclusion here.
        var enabledCount = await CountEnabledAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (enabledCount >= _actionOptions.MaxEnabledActions)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.CapReached, Record: null);
        }

        // Version bumps because State changes; carry the EvalResult through the transition for audit.
        var promoted = await TransitionSuggestedAsync(agentDefinitionId, id, PlaybookActionState.Enabled, pending.EvalResult, cancellationToken).ConfigureAwait(false);
        return promoted is null
            ? new PlaybookPromotionResult(PlaybookPromotionStatus.NotFound, Record: null)
            : new PlaybookPromotionResult(PlaybookPromotionStatus.Promoted, promoted);
    }

    public async Task<PlaybookActionRecord?> RecordEvalResultAsync(Guid agentDefinitionId, Guid id, string evalResultJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalResultJson);

        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }

        // Record the eval JSON only — the action stays Suggested with every injected field (Behavior, Priority, State)
        // and its staging provenance (Analysis/Extracted) unchanged, so the store leaves Version alone (EvalResult is
        // excluded from its config-affecting rule).
        var storeInput = new PlaybookActionInput(pending.AgentDefinitionId,
            PlaybookActionState.Suggested,
            pending.Source,
            pending.TriggerCondition,
            pending.Behavior,
            pending.Scope,
            pending.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            evalResultJson,
            MemoryScope: pending.MemoryScope);

        return await _store.UpdateAsync(id, storeInput, cancellationToken).ConfigureAwait(false);
    }

    public Task<PlaybookActionRecord?> RejectSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Reject moves the action to Archived (provenance preserved rather than hard-deleted). A recorded eval result
        // is irrelevant once archived, so clear it rather than let a stale pass linger on the rejected record.
        return TransitionSuggestedAsync(agentDefinitionId, id, PlaybookActionState.Archived, evalResult: null, cancellationToken);
    }

    public async Task<PlaybookActionRecord?> UpdateSuggestedAsync(SuggestedActionEditInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Behavior))
        {
            throw new PlaybookActionValidationException("Behavior is required.");
        }

        var pending = await LoadPendingSuggestionAsync(input.AgentDefinitionId, input.ActionId, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }

        // The action stays Suggested and keeps its staging provenance (Analysis/Extracted) + evidence + confidence; only
        // the operator-editable fields change. Editing clears any recorded EvalResult (the trailing argument is left
        // null) so a stale pass cannot promote an edited action — the operator must re-run the eval. Promotion remains a
        // separate, explicit step.
        var storeInput = new PlaybookActionInput(pending.AgentDefinitionId,
            PlaybookActionState.Suggested,
            pending.Source,
            input.TriggerCondition,
            input.Behavior,
            input.Scope,
            input.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            MemoryScope: pending.MemoryScope);

        return await _store.UpdateAsync(input.ActionId, storeInput, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // A review action applies only to a pending suggestion owned by the route agent: enforce ownership (IDOR),
        // the Suggested state, and a staging provenance. Both Analysis (feedback-proposed) and Extracted (adaptive-memory
        // post-run mined) candidates are staged suggestions that the SAME governance gate (eval + approve) reviews —
        // anything else (missing, wrong agent, already enabled, manual) returns null → 404.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null
            || existing.AgentDefinitionId != agentDefinitionId
            || existing.State != PlaybookActionState.Suggested
            || !IsStagedSuggestionSource(existing.Source))
        {
            return null;
        }

        return existing;
    }

    /// <summary>
    ///     A staged suggestion is any non-manual candidate awaiting the eval gate + approval. Both feedback-driven
    ///     (<see cref="PlaybookActionSource.Analysis" />) and adaptive-memory extraction
    ///     (<see cref="PlaybookActionSource.Extracted" />) candidates share the same governance lifecycle; the review
    ///     paths preserve whichever provenance the candidate carries rather than rewriting it.
    /// </summary>
    private static bool IsStagedSuggestionSource(PlaybookActionSource source)
    {
        return source is PlaybookActionSource.Analysis or PlaybookActionSource.Extracted;
    }

    private async Task<PlaybookActionRecord?> TransitionSuggestedAsync(Guid agentDefinitionId, Guid id, PlaybookActionState target, string? evalResult, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }

        // Preserve the candidate's staging provenance (Analysis/Extracted) and typed scope through the transition; only
        // the lifecycle State changes (Enabled on promote, Archived on reject).
        var storeInput = new PlaybookActionInput(pending.AgentDefinitionId,
            target,
            pending.Source,
            pending.TriggerCondition,
            pending.Behavior,
            pending.Scope,
            pending.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            evalResult,
            MemoryScope: pending.MemoryScope);

        return await _store.UpdateAsync(id, storeInput, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureBelowEnabledCapAsync(Guid agentDefinitionId, Guid? excludedActionId, CancellationToken cancellationToken)
    {
        var enabled = await _store.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        // When re-enabling an existing action, exclude it from the count so an edit that keeps it Enabled is never
        // double-counted against itself.
        var enabledCount = excludedActionId is { } excludedId
            ? enabled.Count(action => action.Id != excludedId)
            : enabled.Count;

        if (enabledCount >= _actionOptions.MaxEnabledActions)
        {
            throw new PlaybookActionValidationException(
                $"This agent already has the maximum of {_actionOptions.MaxEnabledActions} enabled playbook actions; archive or disable one before enabling another.");
        }
    }

    private async Task<int> CountEnabledAsync(Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var enabled = await _store.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        return enabled.Count;
    }

    private async Task ValidateAsync(PlaybookActionInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Behavior))
        {
            throw new PlaybookActionValidationException("Behavior is required.");
        }

        // Manual authoring creates only manual actions; Analysis is reserved for the deferred self-improvement phase.
        if (input.Source != PlaybookActionSource.Manual)
        {
            throw new PlaybookActionValidationException("Only Manual playbook actions can be authored in this phase.");
        }

        // The full lifecycle is persisted, but Manual authoring accepts only the human-toggleable states; Suggested (analysis
        // proposals) and Archived are reserved for the analysis-review workflow.
        if (input.State is not PlaybookActionState.Enabled and not PlaybookActionState.Disabled)
        {
            throw new PlaybookActionValidationException($"State '{input.State}' is not available in this phase; use Enabled or Disabled.");
        }

        // The FK demands an existing owning agent; reject up front rather than surface a downstream constraint failure.
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }
    }
}
