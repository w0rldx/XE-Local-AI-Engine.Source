namespace XE_Local_AI_Engine.Tests.Agents;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
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
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateRecord(input) with { Id = actionId });
        var stored = CreateRecord(input) with { Id = actionId };
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
             .Returns(CreateRecord(CreateInput(otherAgentId)) with { Id = actionId });

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
             .Returns(CreateRecord(CreateInput(agentId)) with { Id = actionId });
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
             .Returns(CreateRecord(CreateInput(otherAgentId)) with { Id = actionId });

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
        var feedbackIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var input = CreateSuggestionInput(agentId, feedbackIds, confidence: 0.7d);
        var stored = CreateRecord(new PlaybookActionInput(
            agentId,
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
        await store.Received(1).AddAsync(
            Arg.Is<PlaybookActionInput>(stored =>
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
        var input = CreateSuggestionInput(Guid.NewGuid(), new[] { Guid.NewGuid() }, confidence);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WhenAgentDoesNotExist_ThrowsValidationAndDoesNotPersist()
    {
        var service = CreateService(out var store, out _, agentExists: false);
        var input = CreateSuggestionInput(Guid.NewGuid(), new[] { Guid.NewGuid() }, confidence: 0.5d);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(() => service.CreateAnalysisSuggestionAsync(input)).ConfigureAwait(false);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAnalysisSuggestionAsync_WithBlankBehavior_ThrowsValidationAndDoesNotPersist()
    {
        var service = CreateService(out var store, out _, agentExists: true);
        var input = CreateSuggestionInput(Guid.NewGuid(), new[] { Guid.NewGuid() }, confidence: 0.5d) with { Behavior = "   " };

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
             .Returns(CreateRecord(CreateInput(agentId, source: PlaybookActionSource.Analysis)) with { Id = actionId });

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
        var pending = CreateSuggestedRecord(agentId, actionId);
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with { State = PlaybookActionState.Enabled });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.NotNull(result, "Promoting an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(
            actionId,
            Arg.Is<PlaybookActionInput>(stored => stored.State == PlaybookActionState.Enabled && stored.Source == PlaybookActionSource.Analysis),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenMissing_ReturnsNullAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<PlaybookActionRecord?>(null));

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Null(result, "Promoting a missing action must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenDifferentAgent_ReturnsNullAndDoesNotUpdate()
    {
        var routeAgentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(CreateSuggestedRecord(otherAgentId, actionId));

        var result = await service.PromoteSuggestedAsync(routeAgentId, actionId).ConfigureAwait(false);

        AssertEx.Null(result, "Promoting another agent's suggestion must return null (404).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenAlreadyEnabled_ReturnsNullAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with { State = PlaybookActionState.Enabled });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Null(result, "Promoting an already-Enabled action must return null (not a pending suggestion).");
        await store.DidNotReceive().UpdateAsync(Arg.Any<Guid>(), Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task PromoteSuggestedAsync_WhenManualSource_ReturnsNullAndDoesNotUpdate()
    {
        var agentId = Guid.NewGuid();
        var service = CreateService(out var store, out _, agentExists: true);
        var actionId = Guid.NewGuid();
        // A Suggested state but Manual source is not a P3 suggestion — the provenance guard must reject it.
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>())
             .Returns(CreateSuggestedRecord(agentId, actionId) with { Source = PlaybookActionSource.Manual });

        var result = await service.PromoteSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.Null(result, "Promoting a Manual-source action must return null (not an Analysis suggestion).");
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
             .Returns(pending with { State = PlaybookActionState.Archived });

        var result = await service.RejectSuggestedAsync(agentId, actionId).ConfigureAwait(false);

        AssertEx.NotNull(result, "Rejecting an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(
            actionId,
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
        var evidence = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var pending = CreateSuggestedRecord(agentId, actionId) with { SourceFeedbackIds = evidence, Confidence = 0.42d };
        store.GetByIdAsync(actionId, Arg.Any<CancellationToken>()).Returns(pending);
        store.UpdateAsync(actionId, Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(pending with { Behavior = "Edited behavior." });
        var input = new SuggestedActionEditInput(agentId, actionId, "Edited behavior.", TriggerCondition: "new trigger", Scope: "new-scope", Priority: 7);

        var result = await service.UpdateSuggestedAsync(input).ConfigureAwait(false);

        AssertEx.NotNull(result, "Editing an owned pending suggestion should return the updated record.");
        await store.Received(1).UpdateAsync(
            actionId,
            Arg.Is<PlaybookActionInput>(stored =>
                stored.State == PlaybookActionState.Suggested
                && stored.Source == PlaybookActionSource.Analysis
                && stored.Behavior == "Edited behavior."
                && stored.Scope == "new-scope"
                && stored.Priority == 7
                && stored.Confidence.HasValue && Math.Abs(stored.Confidence.Value - 0.42d) < 1e-9
                && stored.SourceFeedbackIds != null
                && stored.SourceFeedbackIds.Count == 2),
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
        return new PlaybookAnalysisSuggestionInput(
            agentDefinitionId,
            "Prefer the existing shared helper over a new one-off.",
            TriggerCondition: null,
            Scope: null,
            Priority: 100,
            feedbackIds,
            confidence);
    }

    private static PlaybookActionRecord CreateSuggestedRecord(Guid agentDefinitionId, Guid actionId)
    {
        return new PlaybookActionRecord(
            actionId,
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
            SourceFeedbackIds: new[] { Guid.NewGuid() },
            Confidence: 0.6d);
    }

    private static PlaybookActionService CreateService(out IPlaybookActionStore store,
        out IAgentDefinitionStore agentStore,
        bool agentExists)
    {
        store = Substitute.For<IPlaybookActionStore>();
        agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                  .Returns(agentExists ? Task.FromResult<AgentDefinitionRecord?>(CreateAgent()) : Task.FromResult<AgentDefinitionRecord?>(null));
        return new PlaybookActionService(store, agentStore);
    }

    private static PlaybookActionInput CreateInput(Guid? agentDefinitionId = null,
        PlaybookActionState state = PlaybookActionState.Enabled,
        PlaybookActionSource source = PlaybookActionSource.Manual,
        string behavior = "Always run the full test suite before reporting complete.")
    {
        return new PlaybookActionInput(
            agentDefinitionId ?? Guid.NewGuid(),
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
