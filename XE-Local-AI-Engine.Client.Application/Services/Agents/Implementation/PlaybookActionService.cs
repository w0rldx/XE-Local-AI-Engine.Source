namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Eval;

internal sealed class PlaybookActionService(IPlaybookActionStore store, IAgentDefinitionStore agentDefinitionStore) : IPlaybookActionService
{
    private readonly IPlaybookActionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));

    public async Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);
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
        // evidence. Analysis (Suggested/Archived) actions are edited only via the dedicated P3 review paths.
        if (existing.Source != PlaybookActionSource.Manual)
        {
            return null;
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

        // §6 #2 — an analysis proposal with no cited evidence is rejected, never stored.
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
        var storeInput = new PlaybookActionInput(
            input.AgentDefinitionId,
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
        // Human-approved staging → active, gated by the Playbook P4 eval result. The ownership/state guard runs first
        // (NotFound → 404), then the eval gate (Eval* → 409), and only an eval that passed and is current flips Enabled.
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.NotFound, null);
        }

        // No eval since authoring/edit → promotion is not yet provable. (UpdateSuggestedAsync clears EvalResult on edit.)
        if (string.IsNullOrWhiteSpace(pending.EvalResult))
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, null);
        }

        PlaybookEvalResult? evalResult;
        try
        {
            evalResult = JsonSerializer.Deserialize<PlaybookEvalResult>(pending.EvalResult, PlaybookEvalResult.SerializerOptions);
        }
        catch (JsonException)
        {
            // A result we cannot read cannot prove no-regression; require a fresh eval rather than promote blindly.
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, null);
        }

        if (evalResult is null)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRequired, null);
        }

        // Staleness backstop behind clear-on-edit: the recorded pass must be for the action's current content snapshot.
        if (evalResult.ActionVersionAtEval != pending.Version)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalStale, null);
        }

        if (!evalResult.Passed)
        {
            return new PlaybookPromotionResult(PlaybookPromotionStatus.EvalRegressed, null);
        }

        // Version bumps because State changes; carry the EvalResult through the transition for audit.
        var promoted = await TransitionSuggestedAsync(agentDefinitionId, id, PlaybookActionState.Enabled, pending.EvalResult, cancellationToken).ConfigureAwait(false);
        return promoted is null
            ? new PlaybookPromotionResult(PlaybookPromotionStatus.NotFound, null)
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

        // Record the eval JSON only — the action stays Suggested/Analysis with every injected field (Behavior, Priority,
        // State) unchanged, so the store leaves Version alone (EvalResult is excluded from its config-affecting rule).
        var storeInput = new PlaybookActionInput(
            pending.AgentDefinitionId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            pending.TriggerCondition,
            pending.Behavior,
            pending.Scope,
            pending.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            evalResultJson);

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

        // The action stays Suggested/Analysis and keeps its evidence + confidence; only the operator-editable fields
        // change. Editing clears any recorded EvalResult (the trailing argument is left null) so a stale pass cannot
        // promote an edited action — the operator must re-run the eval. Promotion remains a separate, explicit step.
        var storeInput = new PlaybookActionInput(
            pending.AgentDefinitionId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            input.TriggerCondition,
            input.Behavior,
            input.Scope,
            input.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            EvalResult: null);

        return await _store.UpdateAsync(input.ActionId, storeInput, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybookActionRecord?> TransitionSuggestedAsync(Guid agentDefinitionId, Guid id, PlaybookActionState target, string? evalResult, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }

        var storeInput = new PlaybookActionInput(
            pending.AgentDefinitionId,
            target,
            PlaybookActionSource.Analysis,
            pending.TriggerCondition,
            pending.Behavior,
            pending.Scope,
            pending.Priority,
            pending.SourceFeedbackIds,
            pending.Confidence,
            evalResult);

        return await _store.UpdateAsync(id, storeInput, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // A review action applies only to a pending suggestion owned by the route agent: enforce ownership (IDOR),
        // the Suggested state, and the Analysis provenance. Anything else (missing, wrong agent, already enabled,
        // manual) returns null → 404.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null
            || existing.AgentDefinitionId != agentDefinitionId
            || existing.State != PlaybookActionState.Suggested
            || existing.Source != PlaybookActionSource.Analysis)
        {
            return null;
        }

        return existing;
    }

    private async Task ValidateAsync(PlaybookActionInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Behavior))
        {
            throw new PlaybookActionValidationException("Behavior is required.");
        }

        // P1 authors only manual actions; Analysis is reserved for the deferred self-improvement phase.
        if (input.Source != PlaybookActionSource.Manual)
        {
            throw new PlaybookActionValidationException("Only Manual playbook actions can be authored in this phase.");
        }

        // The full lifecycle is persisted, but P1 accepts only the human-toggleable states; Suggested (analysis
        // proposals) and Archived are reserved for later phases.
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
