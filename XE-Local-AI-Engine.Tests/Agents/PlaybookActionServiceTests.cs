namespace XE_Local_AI_Engine.Tests.Agents;

using System.Text.Json;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaybookActionServiceTests
{
    [Test]
    public async Task CreateAsync_WithValidInput_PersistsThroughStore()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateInput();
        var stored = CreateRecord(input);
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.Equal(stored.Id, result.Id);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithBlankBehavior_ThrowsValidation()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateInput(behavior: "   ");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenAgentDoesNotExist_ThrowsValidation()
    {
        var service = CreateService(out var store, out _, agentExists: false);
        var input = CreateInput();

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithSuggestedState_ThrowsValidation()
    {
        var service = CreateService(out _, out _, agentExists: true);
        var input = CreateInput(state: PlaybookActionState.Suggested);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithArchivedState_ThrowsValidation()
    {
        var service = CreateService(out _, out _, agentExists: true);
        var input = CreateInput(state: PlaybookActionState.Archived);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WithAnalysisSource_ThrowsValidation()
    {
        var service = CreateService(out _, out _, agentExists: true);
        var input = CreateInput(source: PlaybookActionSource.Analysis);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenActionBelongsToRouteAgent_DelegatesToStore()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var input = CreateInput(agentId, state: PlaybookActionState.Disabled);
        // The action already belongs to the same (route) agent, so the ownership guard passes through to the store.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateRecord(input) with
        {
            Id = actionId
        });
        var stored = CreateRecord(input) with
        {
            Id = actionId
        };
        store.UpdateAsync(actionId, input, Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.UpdateAsync(actionId, input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.Equal(actionId, result!.Id);
        await store.Received(1).UpdateAsync(actionId, input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenActionBelongsToDifferentAgent_ReturnsNullAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var input = CreateInput(routeAgentId);
        // The stored action belongs to a DIFFERENT agent than the route — the IDOR guard must reject it.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateRecord(CreateInput(otherAgentId)) with
             {
                 Id = actionId
             });

        var result = await service.UpdateAsync(actionId, input).ConfigureAwait(false);

        AssertEx.Null(result, "Updating another agent's action via this agent's route must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenActionMissing_ReturnsNullAndDoesNotUpdate()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var input = CreateInput();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PlaybookActionRecord?>(null));

        var result = await service.UpdateAsync(actionId, input).ConfigureAwait(false);

        AssertEx.Null(result, "Updating a missing action must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenActionBelongsToRouteAgent_DelegatesToStore()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateRecord(CreateInput(agentId)) with
             {
                 Id = actionId
             });
        store.DeleteAsync(actionId, Arg.Any<CancellationToken>()).Returns(true);

        var deleted = await service.DeleteAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.True(deleted, "DeleteAsync should report the store result when the action belongs to the route agent.");
        await store.Received(1).DeleteAsync(actionId, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenActionBelongsToDifferentAgent_ReturnsFalseAndDoesNotDelete()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateRecord(CreateInput(otherAgentId)) with
             {
                 Id = actionId
             });

        var deleted = await service.DeleteAsync(routeAgentId, actionId).ConfigureAwait(false);

        AssertEx.False(deleted, "Deleting another agent's action via this agent's route must return false (404).");
        await store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenActionMissing_ReturnsFalseAndDoesNotDelete()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PlaybookActionRecord?>(null));

        var deleted = await service.DeleteAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.False(deleted, "Deleting a missing action must return false (404).");
        await store.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ListByAgentAsync_DelegatesToStore()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var agentId = Guid.NewGuid();
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([CreateRecord(CreateInput(agentId))]));

        var list = await service.ListByAgentAsync(agentId).ConfigureAwait(false);

        AssertEx.Equal(1, list.Count);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WithValidInput_PersistsSuggestedAnalysisActionWithEvidence()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var feedbackIds = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        var input = CreateSuggestionInput(agentId, feedbackIds, confidence: 0.7d);
        var stored = CreateRecord(new PlaybookActionInput(agentId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            input.TriggerCondition,
            input.Behavior,
            input.Scope,
            input.Priority,
            feedbackIds,
            input.Confidence));
        store.AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).Returns(stored);

        var result = await service.CreateAnalysisSuggestionAsync(input).ConfigureAwait(false);

        AssertEx.Equal(stored.Id, result.Id);
        await store.Received(1).AddAsync(Arg.Is<PlaybookActionInput>(stored =>
                stored.State == PlaybookActionState.Suggested
                && stored.Source == PlaybookActionSource.Analysis
                && stored.AgentDefinitionId == agentId
                && stored.Behavior == input.Behavior
                && stored.Confidence.HasValue && Math.Abs(stored.Confidence.Value - 0.7d) < 1e-9
                && stored.SourceFeedbackIds != null
                && stored.SourceFeedbackIds.Count == 2),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WithEmptyEvidence_ThrowsValidationAndDoesNotPersist()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateSuggestionInput(Guid.NewGuid(), feedbackIds: [], confidence: 0.5d);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    [Arguments(-0.01d)]
    [Arguments(1.01d)]
    [Arguments(double.NaN)]
    public async Task CreateAnalysisSuggestionAsync_WithConfidenceOutOfRange_ThrowsValidationAndDoesNotPersist(double confidence)
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateSuggestionInput(Guid.NewGuid(), new[]
        {
            Guid.NewGuid()
        }, confidence);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WhenAgentDoesNotExist_ThrowsValidationAndDoesNotPersist()
    {
        var service = CreateService(out var store, out _, agentExists: false);
        var input = CreateSuggestionInput(Guid.NewGuid(), new[]
        {
            Guid.NewGuid()
        }, confidence: 0.5d);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WithBlankBehavior_ThrowsValidationAndDoesNotPersist()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateSuggestionInput(Guid.NewGuid(), new[]
            {
                Guid.NewGuid()
            }, confidence: 0.5d) with
            {
                Behavior = "   "
            };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenExistingActionIsAnalysisSource_ReturnsNullAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var input = CreateInput(agentId);
        // The manual route may not touch an Analysis-provenance action even when ownership matches.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateRecord(CreateInput(agentId, source: PlaybookActionSource.Analysis)) with
             {
                 Id = actionId
             });

        var result = await service.UpdateAsync(actionId, input).ConfigureAwait(false);

        AssertEx.Null(result, "The manual route must not update an Analysis action (returns null/404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenOwnedPendingSuggestion_UpdatesToEnabled()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // The gate now requires a passing eval matching the action's current Version before promotion is allowed.
        var pending = CreateSuggestedRecord(agentId, actionId) with
        {
            EvalResult = PassingEvalResultJson(version: 1)
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with
             {
                 State = PlaybookActionState.Enabled
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.Promoted, result.Status);
        AssertEx.NotNull(result.Record, "A passing eval should promote the action and return the updated record.");
        await store.Received(1).UpdateAsync(actionId,
            Arg.Is<PlaybookActionInput>(stored =>
                stored.State == PlaybookActionState.Enabled
                && stored.Source == PlaybookActionSource.Analysis
                && stored.EvalResult == pending.EvalResult),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenAtEnabledCap_ReturnsCapReachedAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        // Cap of 2 with two already-Enabled actions: the eval passes, but the hard cap blocks the promote with no write.
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 2);
        var actionId = Guid.NewGuid();
        var pending = CreateSuggestedRecord(agentId, actionId) with
        {
            EvalResult = PassingEvalResultJson(version: 1)
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledRecord(agentId), EnabledRecord(agentId)]));

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.CapReached, result.Status);
        AssertEx.True(result.Record is null, "A cap-blocked promote returns no record.");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenUnderEnabledCap_PromotesToEnabled()
    {
        var agentId = Guid.NewGuid();
        // Cap of 2 with one already-Enabled action: under the cap, the passing eval promotes normally.
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 2);
        var actionId = Guid.NewGuid();
        var pending = CreateSuggestedRecord(agentId, actionId) with
        {
            EvalResult = PassingEvalResultJson(version: 1)
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledRecord(agentId)]));
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with
             {
                 State = PlaybookActionState.Enabled
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.Promoted, result.Status);
        await store.Received(1).UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenEnabledAtCap_ThrowsValidationAndDoesNotAdd()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 1);
        var input = CreateInput(agentId, state: PlaybookActionState.Enabled);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledRecord(agentId)]));

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenDisabledAtCap_PersistsThroughStore()
    {
        var agentId = Guid.NewGuid();
        // A create-as-Disabled never touches the Enabled cap, even when the agent is already at it.
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 1);
        var input = CreateInput(agentId, state: PlaybookActionState.Disabled);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledRecord(agentId)]));
        store.AddAsync(input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input));

        var result = await service.CreateAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).AddAsync(input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenDisabledToEnabledAtCap_ThrowsValidationAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 1);
        var actionId = Guid.NewGuid();
        // The action being updated is currently Disabled; the agent is already at the Enabled cap with a DIFFERENT action.
        var existing = CreateRecord(CreateInput(agentId, state: PlaybookActionState.Disabled)) with
        {
            Id = actionId
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([EnabledRecord(agentId)]));
        var input = CreateInput(agentId, state: PlaybookActionState.Enabled);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.UpdateAsync(actionId, input)).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateAsync_WhenEditingAlreadyEnabledActionAtCap_DelegatesToStore()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true, maxEnabledActions: 1);
        var actionId = Guid.NewGuid();
        // The action is ALREADY Enabled and stays Enabled (an edit, not a transition into Enabled). Even at the cap it
        // must not be blocked — the cap guard fires only on a non-Enabled -> Enabled transition.
        var existing = CreateRecord(CreateInput(agentId, state: PlaybookActionState.Enabled)) with
        {
            Id = actionId
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(existing);
        store.ListEnabledByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([existing]));
        var input = CreateInput(agentId, state: PlaybookActionState.Enabled, behavior: "An edited behavior.");
        store.UpdateAsync(actionId, input, Arg.Any<CancellationToken>()).Returns(CreateRecord(input) with
        {
            Id = actionId
        });

        var result = await service.UpdateAsync(actionId, input).ConfigureAwait(false);

        AssertEx.NotNull(result);
        await store.Received(1).UpdateAsync(actionId, input, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenMissing_ReturnsNotFoundAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PlaybookActionRecord?>(null));

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.NotFound, result.Status);
        AssertEx.Null(result.Record, "A missing action must not return a record (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenDifferentAgent_ReturnsNotFoundAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(otherAgentId, actionId));

        var result = await service.PromoteSuggestedAsync(routeAgentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.NotFound, result.Status);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenAlreadyEnabled_ReturnsNotFoundAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with
             {
                 State = PlaybookActionState.Enabled
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.NotFound, result.Status);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenManualSource_ReturnsNotFoundAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // A Suggested state with Manual source is not a generated suggestion, so the provenance guard must reject it.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with
             {
                 Source = PlaybookActionSource.Manual
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.NotFound, result.Status);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenNoEvalRecorded_ReturnsEvalRequiredAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // No EvalResult recorded (null) — the gate blocks with EvalRequired.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(agentId, actionId));

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.EvalRequired, result.Status);
        AssertEx.Null(result.Record);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenEvalFailed_ReturnsEvalRegressedAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // A recorded eval for the current Version but Passed == false — the gate blocks with EvalRegressed.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with
             {
                 EvalResult = FailingEvalResultJson(version: 1)
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.EvalRegressed, result.Status);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenEvalForOlderVersion_ReturnsEvalStaleAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // A passing eval, but recorded for an older content snapshot (Version 1) than the action's current Version (2).
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with
             {
                 Version = 2,
                 EvalResult = PassingEvalResultJson(version: 1)
             });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Equal(PlaybookPromotionStatus.EvalStale, result.Status);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task RecordEvalResultAsync_WhenOwnedPendingSuggestion_StoresEvalResultAndStaysSuggested()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var pending = CreateSuggestedRecord(agentId, actionId);
        var json = PassingEvalResultJson(version: 1);
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with
             {
                 EvalResult = json
             });

        var result = await service.RecordEvalResultAsync(agentId, actionId, json).ConfigureAwait(false);

        AssertEx.NotNull(result, "Recording an eval on an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(actionId,
            Arg.Is<PlaybookActionInput>(stored =>
                stored.State == PlaybookActionState.Suggested
                && stored.Source == PlaybookActionSource.Analysis
                && stored.Behavior == pending.Behavior
                && stored.Priority == pending.Priority
                && stored.EvalResult == json),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task RecordEvalResultAsync_WhenDifferentAgent_ReturnsNullAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(otherAgentId, actionId));

        var result = await service.RecordEvalResultAsync(routeAgentId, actionId, PassingEvalResultJson(version: 1)).ConfigureAwait(false);

        AssertEx.Null(result, "Recording an eval on another agent's suggestion must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task RejectSuggestedAsync_WhenOwnedPendingSuggestion_UpdatesToArchived()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var pending = CreateSuggestedRecord(agentId, actionId);
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with
             {
                 State = PlaybookActionState.Archived
             });

        var result = await service.RejectSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.NotNull(result, "Rejecting an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(actionId,
            Arg.Is<PlaybookActionInput>(stored => stored.State == PlaybookActionState.Archived && stored.Source == PlaybookActionSource.Analysis),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task RejectSuggestedAsync_WhenDifferentAgent_ReturnsNullAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(otherAgentId, actionId));

        var result = await service.RejectSuggestedAsync(routeAgentId, actionId).ConfigureAwait(false);

        AssertEx.Null(result, "Rejecting another agent's suggestion must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateSuggestedAsync_EditsFieldsButKeepsSuggestedAnalysisAndEvidence()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        var evidence = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid()
        };
        var pending = CreateSuggestedRecord(agentId, actionId) with
        {
            SourceFeedbackIds = evidence,
            Confidence = 0.42d
        };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with
             {
                 Behavior = "Edited behavior."
             });
        var input = new SuggestedActionEditInput(agentId, actionId, "Edited behavior.", TriggerCondition: "new trigger", Scope: "new-scope", Priority: 7);

        var result = await service.UpdateSuggestedAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result, "Editing an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(actionId,
            Arg.Is<PlaybookActionInput>(stored =>
                stored.State == PlaybookActionState.Suggested
                && stored.Source == PlaybookActionSource.Analysis
                && stored.Behavior == "Edited behavior."
                && stored.Scope == "new-scope"
                && stored.Priority == 7
                && stored.Confidence.HasValue && Math.Abs(stored.Confidence.Value - 0.42d) < 1e-9
                && stored.SourceFeedbackIds != null
                && stored.SourceFeedbackIds.Count == 2
                // Editing invalidates any prior eval pass: the store input must clear EvalResult.
                && stored.EvalResult == null),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateSuggestedAsync_WhenDifferentAgent_ReturnsNullAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(otherAgentId, actionId));
        var input = new SuggestedActionEditInput(routeAgentId, actionId, "Edited behavior.", TriggerCondition: null, Scope: null, Priority: 1);

        var result = await service.UpdateSuggestedAsync(input).ConfigureAwait(false);

        AssertEx.Null(result, "Editing another agent's suggestion must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateSuggestedAsync_WithBlankBehavior_ThrowsValidation()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var input = new SuggestedActionEditInput(agentId, Guid.NewGuid(), "   ", TriggerCondition: null, Scope: null, Priority: 1);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.UpdateSuggestedAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static PlaybookAnalysisSuggestionInput CreateSuggestionInput(Guid agentDefinitionId,
        IReadOnlyList<Guid> feedbackIds,
        double confidence)
    {
        return new PlaybookAnalysisSuggestionInput(agentDefinitionId,
            "Prefer the existing shared helper over a new one-off.",
            TriggerCondition: null,
            Scope: null,
            Priority: 100,
            feedbackIds,
            confidence);
    }

    private static PlaybookActionRecord CreateSuggestedRecord(Guid agentDefinitionId, Guid actionId)
    {
        return new PlaybookActionRecord(actionId,
            agentDefinitionId,
            PlaybookActionState.Suggested,
            PlaybookActionSource.Analysis,
            TriggerCondition: null,
            "A suggested behavior.",
            Scope: null,
            Priority: 100,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            SourceFeedbackIds: new[]
            {
                Guid.NewGuid()
            },
            Confidence: 0.6d);
    }

    private static string PassingEvalResultJson(int version)
    {
        return EvalResultJson(passed: true, version);
    }

    private static string FailingEvalResultJson(int version)
    {
        return EvalResultJson(passed: false, version);
    }

    private static string EvalResultJson(bool passed, int version)
    {
        var result = new PlaybookEvalResult(passed,
            EvaluatedAtUtc: 1_000,
            ActionVersionAtEval: version,
            ModelName: "test-model",
            GoldenCaseCount: 1,
            GoldenCaseTotal: 1,
            BaselinePassCount: 1,
            CandidatePassCount: passed ? 1 : 0,
            RegressedCaseCount: passed ? 0 : 1,
            ImprovedCaseCount: 0,
            Cases: []);
        return JsonSerializer.Serialize(result, PlaybookEvalResult.SerializerOptions);
    }

    private static PlaybookActionService CreateService(out IPlaybookActionStore store,
        out IAgentDefinitionStore agentStore,
        bool agentExists,
        int maxEnabledActions = 20)
    {
        store = Substitute.For<IPlaybookActionStore>();
        agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                  .Returns(agentExists ? Task.FromResult<AgentDefinitionRecord?>(CreateAgent()) : Task.FromResult<AgentDefinitionRecord?>(null));
        // Default: no enabled actions, so the cap gate is inert unless a test seeds ListEnabledByAgentAsync.
        store.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var actionOptions = Options.Create(new PlaybookActionOptions
        {
            MaxEnabledActions = maxEnabledActions
        });
        return new PlaybookActionService(store, agentStore, actionOptions);
    }

    private static PlaybookActionInput CreateInput(Guid? agentDefinitionId = null,
        PlaybookActionState state = PlaybookActionState.Enabled,
        PlaybookActionSource source = PlaybookActionSource.Manual,
        string behavior = "Always run the full test suite before reporting complete.")
    {
        return new PlaybookActionInput(agentDefinitionId ?? Guid.NewGuid(),
            state,
            source,
            TriggerCondition: null,
            behavior,
            Scope: null,
            Priority: 10);
    }

    private static PlaybookActionRecord CreateRecord(PlaybookActionInput input)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            input.AgentDefinitionId,
            input.State,
            input.Source,
            input.TriggerCondition,
            input.Behavior,
            input.Scope,
            input.Priority,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static PlaybookActionRecord EnabledRecord(Guid agentDefinitionId)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            "An already-enabled behavior.",
            Scope: null,
            Priority: 10,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    private static AgentDefinitionRecord CreateAgent()
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            "Builder",
            Description: null,
            "Instructions.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
