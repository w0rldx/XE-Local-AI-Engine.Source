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
