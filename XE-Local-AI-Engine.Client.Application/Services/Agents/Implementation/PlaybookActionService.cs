namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;

internal sealed class PlaybookActionService : IPlaybookActionService
{
    private readonly PlaybookActionOptions _actionOptions;
    private readonly IAgentDefinitionStore _agentDefinitionStore;
    private readonly PlaybookEvalOptions _evalOptions;
    private readonly IGoldenConversationStore _goldenConversationStore;
    private readonly IEvalModelIdentityResolver _modelIdentityResolver;
    private readonly IPlaybookActionStore _store;

    public PlaybookActionService(IPlaybookActionStore store,
        IAgentDefinitionStore agentDefinitionStore,
        IGoldenConversationStore goldenConversationStore,
        IEvalModelIdentityResolver modelIdentityResolver,
        IOptions<PlaybookActionOptions> actionOptions,
        IOptions<PlaybookEvalOptions> evalOptions)
    {
        _actionOptions = (actionOptions ?? throw new ArgumentNullException(nameof(actionOptions))).Value;
        ArgumentNullException.ThrowIfNull(agentDefinitionStore);
        _agentDefinitionStore = agentDefinitionStore;
        _evalOptions = (evalOptions ?? throw new ArgumentNullException(nameof(evalOptions))).Value;
        ArgumentNullException.ThrowIfNull(goldenConversationStore);
        _goldenConversationStore = goldenConversationStore;
        ArgumentNullException.ThrowIfNull(modelIdentityResolver);
        _modelIdentityResolver = modelIdentityResolver;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken);

        // Manual create-as-Enabled is the second path into Enabled (alongside promote); enforce the same hard cap so it
        // cannot be bypassed via direct CRUD. A create-as-Disabled never touches the cap.
        if (input.State == PlaybookActionState.Enabled)
        {
            await EnsureBelowEnabledCapAsync(input.AgentDefinitionId, excludedActionId: null, cancellationToken);
        }

        return await _store.AddAsync(input, cancellationToken);
    }

    public async Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken);

        // Ownership guard: the action must already belong to the route's agent. A mismatch or a missing action
        // answers null, which the endpoint maps to 404 — this is what blocks the nested-route IDOR.
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null || existing.AgentDefinitionId != input.AgentDefinitionId)
        {
            return null;
        }

        // The manual route never touches an analysis-provenance action: its mapper pins a Manual source, so updating
        // one would rewrite the provenance and drop its evidence. Those are edited only through the review paths.
        if (existing.Source != PlaybookActionSource.Manual)
        {
            return null;
        }

        // Manual Disabled->Enabled is a transition INTO Enabled; enforce the hard cap (excluding this action from the
        // count). Editing an action that is already Enabled (stays Enabled) is not a transition and is never blocked.
        if (existing.State != PlaybookActionState.Enabled && input.State == PlaybookActionState.Enabled)
        {
            await EnsureBelowEnabledCapAsync(input.AgentDefinitionId, id, cancellationToken);
        }

        return await _store.UpdateAsync(id, input, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Same ownership guard as UpdateAsync: only delete the action when it belongs to the route agent, so one
        // agent's playbook route cannot delete another agent's action.
        var existing = await _store.GetByIdAsync(id, cancellationToken);
        if (existing is null || existing.AgentDefinitionId != agentDefinitionId)
        {
            return false;
        }

        return await _store.DeleteAsync(id, cancellationToken);
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

        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }

        // State/Source are pinned here (Suggested/Analysis) — never client-supplied — so the manual CRUD route stays
        // the only path that authors Manual actions, and a suggestion stays inert until a human promotes it.
        var storeInput = new PlaybookActionInput
        {
            AgentDefinitionId = input.AgentDefinitionId,
            State = PlaybookActionState.Suggested,
            Source = PlaybookActionSource.Analysis,
            TriggerCondition = input.TriggerCondition,
            Behavior = input.Behavior,
            Scope = input.Scope,
            Priority = input.Priority,
            SourceFeedbackIds = input.SourceFeedbackIds,
            Confidence = input.Confidence
        };

        return await _store.AddAsync(storeInput, cancellationToken);
    }

    public async Task<PlaybookPromotionResult> PromoteSuggestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Human-approved staging → active, gated by the golden-conversation evaluation result. The ownership/state
        // guard runs first (NotFound → 404), then evaluation status (Eval* → 409), and only a passed, current eval flips Enabled.
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken);
        if (pending is null)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.NotFound,
                Record = null
            };
        }

        // No eval since authoring/edit → promotion is not yet provable. (UpdateSuggestedAsync clears EvalResult on edit.)
        if (string.IsNullOrWhiteSpace(pending.EvalResult))
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalRequired,
                Record = null
            };
        }

        PlaybookEvalResult? evalResult;
        try
        {
            evalResult = JsonSerializer.Deserialize<PlaybookEvalResult>(pending.EvalResult, PlaybookEvalResult.SerializerOptions);
        }
        catch (JsonException)
        {
            // A result we cannot read cannot prove no-regression; require a fresh eval rather than promote blindly.
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalRequired,
                Record = null
            };
        }

        if (evalResult is null)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalRequired,
                Record = null
            };
        }

        // Staleness backstop behind clear-on-edit: the recorded pass must be for the action's current content snapshot.
        if (evalResult.ActionVersionAtEval != pending.Version)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalStale,
                Record = null
            };
        }

        // Completeness: a run that evaluated only a subset of the enabled golden cases (the per-run cap truncated the
        // set) cannot prove no-regression across the whole suite, so a subset pass never authorizes promotion.
        if (evalResult.GoldenCaseCount < evalResult.GoldenCaseTotal)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalIncomplete,
                Record = null
            };
        }

        // The recorded eval must reflect the CURRENT behaviour-affecting context, so recompute over base instructions,
        // sibling actions, the golden set and the model. A legacy result with no fingerprint reads as stale: safe.
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(agentDefinitionId, cancellationToken);
        if (owningAgent is null)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.NotFound,
                Record = null
            };
        }

        var enabledActions = await _store.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken);
        var enabledGoldenCases = await _goldenConversationStore.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken);
        // Resolve the model's weight identity so a same-name weight swap moves the fingerprint. The SAME resolver the
        // eval writer used, including its unverified sentinel, or a verified eval stops matching at promote time.
        var modelIdentity = await _modelIdentityResolver.ResolveAsync(_evalOptions.ModelName, cancellationToken);
        var currentFingerprint = PlaybookEvalFingerprint.Compute(pending.Id,
            pending.Version,
            owningAgent.Instructions,
            enabledActions,
            enabledGoldenCases,
            _evalOptions.ModelName,
            modelIdentity.Token);
        if (!string.Equals(currentFingerprint, evalResult.EvaluationFingerprint, StringComparison.Ordinal))
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalStale,
                Record = null
            };
        }

        if (!evalResult.Passed)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalRegressed,
                Record = null
            };
        }

        // Absolute quality floor, defence in depth: the writer folds this into Passed, but a legacy or hand-crafted
        // result could claim Passed with zero candidate passes, and a run where every case failed proves nothing.
        if (evalResult.CandidatePassCount <= 0)
        {
            return new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalRegressed,
                Record = null
            };
        }

        // Atomic promote under optimistic concurrency and the cap: threading the validated Version closes the TOCTOU
        // where a concurrent edit is promoted on stale evidence, and the cap is re-checked in the same transaction.
        var commit = await _store.PromoteSuggestedIfCurrentAsync(id, pending.Version, _actionOptions.MaxEnabledActions, pending.EvalResult, cancellationToken);
        return commit.Status switch
        {
            PlaybookPromotionCommitStatus.Committed => new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.Promoted,
                Record = commit.Record
            },
            PlaybookPromotionCommitStatus.CapReached => new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.CapReached,
                Record = null
            },
            // A version/state mismatch means a concurrent edit/promote changed the row after the eval evidence was
            // validated; surface the existing stale-eval conflict so the operator re-runs the eval on the current snapshot.
            PlaybookPromotionCommitStatus.VersionConflict => new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.EvalStale,
                Record = null
            },
            _ => new PlaybookPromotionResult
            {
                Status = PlaybookPromotionStatus.NotFound,
                Record = null
            }
        };
    }

    public async Task<PlaybookActionRecord?> RecordEvalResultAsync(Guid agentDefinitionId, Guid id, string evalResultJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalResultJson);

        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        // The eval JSON only: the action keeps every injected field and its staging provenance, so the store leaves
        // Version alone — EvalResult is excluded from its config-affecting rule.
        var storeInput = new PlaybookActionInput
        {
            AgentDefinitionId = pending.AgentDefinitionId,
            State = PlaybookActionState.Suggested,
            Source = pending.Source,
            TriggerCondition = pending.TriggerCondition,
            Behavior = pending.Behavior,
            Scope = pending.Scope,
            Priority = pending.Priority,
            SourceFeedbackIds = pending.SourceFeedbackIds,
            Confidence = pending.Confidence,
            EvalResult = evalResultJson,
            MemoryScope = pending.MemoryScope
        };

        return await _store.UpdateAsync(id, storeInput, cancellationToken);
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

        var pending = await LoadPendingSuggestionAsync(input.AgentDefinitionId, input.ActionId, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        // The action keeps its Suggested state, staging provenance, evidence and confidence; only operator-editable
        // fields change. The cleared EvalResult is what stops a stale pass promoting an edited action.
        var storeInput = new PlaybookActionInput
        {
            AgentDefinitionId = pending.AgentDefinitionId,
            State = PlaybookActionState.Suggested,
            Source = pending.Source,
            TriggerCondition = input.TriggerCondition,
            Behavior = input.Behavior,
            Scope = input.Scope,
            Priority = input.Priority,
            SourceFeedbackIds = pending.SourceFeedbackIds,
            Confidence = pending.Confidence,
            MemoryScope = pending.MemoryScope
        };

        return await _store.UpdateAsync(input.ActionId, storeInput, cancellationToken);
    }

    public async Task<PlaybookActionRecord?> LoadPendingSuggestionAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // A review action applies only to a pending suggestion owned by the route agent, so ownership, the Suggested
        // state and a staging provenance are all enforced; anything else answers null and the endpoint maps it to 404.
        var existing = await _store.GetByIdAsync(id, cancellationToken);
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
    ///     A staged suggestion is any non-manual candidate awaiting the eval gate and approval.
    /// </summary>
    /// <remarks>
    ///     Feedback-driven and adaptive-memory candidates share one governance lifecycle, and the review paths
    ///     preserve whichever provenance a candidate carries rather than rewriting it.
    /// </remarks>
    private static bool IsStagedSuggestionSource(PlaybookActionSource source)
    {
        return source is PlaybookActionSource.Analysis or PlaybookActionSource.Extracted;
    }

    private async Task<PlaybookActionRecord?> TransitionSuggestedAsync(Guid agentDefinitionId, Guid id, PlaybookActionState target, string? evalResult, CancellationToken cancellationToken)
    {
        var pending = await LoadPendingSuggestionAsync(agentDefinitionId, id, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        // Preserve the candidate's staging provenance (Analysis/Extracted) and typed scope through the transition; only
        // the lifecycle State changes (Enabled on promote, Archived on reject).
        var storeInput = new PlaybookActionInput
        {
            AgentDefinitionId = pending.AgentDefinitionId,
            State = target,
            Source = pending.Source,
            TriggerCondition = pending.TriggerCondition,
            Behavior = pending.Behavior,
            Scope = pending.Scope,
            Priority = pending.Priority,
            SourceFeedbackIds = pending.SourceFeedbackIds,
            Confidence = pending.Confidence,
            EvalResult = evalResult,
            MemoryScope = pending.MemoryScope
        };

        return await _store.UpdateAsync(id, storeInput, cancellationToken);
    }

    private async Task EnsureBelowEnabledCapAsync(Guid agentDefinitionId, Guid? excludedActionId, CancellationToken cancellationToken)
    {
        var enabled = await _store.ListEnabledByAgentAsync(agentDefinitionId, cancellationToken);
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
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }
    }
}
